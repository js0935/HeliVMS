import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createLiveViewer, fetchDeployment, waitForFirstFrame, waitForIceGathering, whepPath } from './live.js';

/**
 * M244 WHEP 客戶端測試。
 *
 * 這些測試存在的理由：live.js 走的是「offer→answer」這條最容易壞掉、
 * 卻最難用人工點擊穩定重現的路徑。特別是「不做 trickle 就必須等 ICE 收集」
 * ——少了那個等待，症狀是畫面偶爾開不起來，且伺服器端日誌完全正常。
 */

/** 假的 RTCPeerConnection，只實作 WHEP 協商真正會碰到的部分。 */
class FakePeer {
  static instances = [];

  constructor() {
    this.iceGatheringState = 'complete'; // 預設已完成，讓測試聚焦在協商本身
    this.localDescription = null;
    this.remoteDescription = null;
    this.transceivers = [];
    this.connectionState = 'new';
    this.closed = false;
    this.handlers = {};
    FakePeer.instances.push(this);
  }

  addTransceiver(kind, init) {
    this.transceivers.push({ kind, ...init });
  }

  async createOffer() {
    return { type: 'offer', sdp: 'v=0\r\n' };
  }

  async setLocalDescription(desc) {
    this.localDescription = desc;
  }

  async setRemoteDescription(desc) {
    this.remoteDescription = desc;
  }

  addEventListener(name, fn) {
    (this.handlers[name] ??= []).push(fn);
  }

  removeEventListener(name, fn) {
    this.handlers[name] = (this.handlers[name] ?? []).filter((h) => h !== fn);
  }

  close() {
    this.closed = true;
  }
}

const fakeVideo = () => ({ srcObject: 'unset' });

/**
 * 假的 <video>：可以真的收到影格（videoWidth > 0）與派發事件。
 *
 * 這是「已連線但沒有畫面」那條路徑唯一的觀測點——真瀏覽器裡 videoWidth 只有在真的解出
 * 一張影格後才會變成非零，所以它就是「有沒有畫面」的判準。
 */
function fakeVideoEl() {
  const listeners = {};
  return {
    srcObject: 'unset',
    videoWidth: 0,
    videoHeight: 0,
    addEventListener(name, fn) {
      (listeners[name] ??= []).push(fn);
    },
    removeEventListener(name, fn) {
      listeners[name] = (listeners[name] ?? []).filter((h) => h !== fn);
    },
    /** 派發事件（不動尺寸）。 */
    dispatch(event) {
      for (const fn of listeners[event] ?? []) fn();
    },
    /** 模擬「第一張影格到了」：先設定尺寸再派發事件（順序與真瀏覽器一致）。 */
    frame(width = 640, height = 360, event = 'loadeddata') {
      this.videoWidth = width;
      this.videoHeight = height;
      for (const fn of listeners[event] ?? []) fn();
    },
    listenerCount() {
      return Object.values(listeners).reduce((n, list) => n + list.length, 0);
    },
  };
}

const identity = (key) => key;

function viewer(overrides = {}) {
  return createLiveViewer({ apiRaw: overrides.apiRaw ?? vi.fn(), t: identity });
}

beforeEach(() => {
  FakePeer.instances = [];
  globalThis.RTCPeerConnection = FakePeer;
  globalThis.MediaStream = class {
    constructor() {
      this.id = 'stream';
    }
  };
});

afterEach(() => {
  delete globalThis.RTCPeerConnection;
  delete globalThis.MediaStream;
});

describe('waitForIceGathering', () => {
  it('已完成時立即返回', async () => {
    const peer = new FakePeer();
    await expect(waitForIceGathering(peer, 50)).resolves.toBeUndefined();
  });

  it('狀態變成 complete 就返回', async () => {
    const peer = new FakePeer();
    peer.iceGatheringState = 'gathering';

    const waiting = waitForIceGathering(peer, 1000);
    peer.iceGatheringState = 'complete';
    peer.handlers.icegatheringstatechange.forEach((fn) => fn());

    await expect(waiting).resolves.toBeUndefined();
  });

  it('永遠收不到候選時逾時返回，不會卡住', async () => {
    // 這個行為是刻意的：卡住比「讓伺服器端用自己的 STUN 產生候選」更糟。
    const peer = new FakePeer();
    peer.iceGatheringState = 'gathering';

    await expect(waitForIceGathering(peer, 20)).resolves.toBeUndefined();
  });

  it('逾時後移除事件監聽，避免洩漏', async () => {
    const peer = new FakePeer();
    peer.iceGatheringState = 'gathering';

    await waitForIceGathering(peer, 20);

    expect(peer.handlers.icegatheringstatechange).toHaveLength(0);
  });
});

describe('whepPath', () => {
  it('多分割要次碼流時帶上參數', () => {
    expect(whepPath(7, 'sub')).toBe('/api/stream/7/whep?stream=sub');
  });

  it('單格或未指定時不帶參數（舊版伺服器行為不變）', () => {
    // 少了這一條，升級前端但沒升級伺服器時會變成 404／被忽略的查詢字串，
    // 而畫面空白沒有任何線索。
    expect(whepPath(7, 'main')).toBe('/api/stream/7/whep');
    expect(whepPath(7, undefined)).toBe('/api/stream/7/whep');
  });
});

describe('createLiveViewer.open', () => {
  it('拒絕非正整數的通道', async () => {
    const apiRaw = vi.fn();
    const notify = vi.fn();
    const v = viewer({ apiRaw });

    await expect(v.open(0, fakeVideo(), notify)).resolves.toBe(false);
    expect(notify).toHaveBeenCalledWith('state.channelPositive');
    expect(apiRaw).not.toHaveBeenCalled();
  });

  it('沒有 WebRTC 時直接說明原因', async () => {
    delete globalThis.RTCPeerConnection;
    const notify = vi.fn();
    const v = viewer();

    await expect(v.open(1, fakeVideo(), notify)).resolves.toBe(false);
    expect(notify).toHaveBeenCalledWith('state.webrtcUnsupported');
  });

  it('送出 offer 並套用 answer', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/api/stream/whep/abc' }),
      text: async () => 'v=0\r\na=answer\r\n',
    }));
    const video = fakeVideo();
    const v = viewer({ apiRaw });

    await expect(v.open(7, video, vi.fn())).resolves.toBe(true);

    const peer = FakePeer.instances[0];
    expect(peer.localDescription.sdp).toContain('v=0');
    expect(peer.remoteDescription).toEqual({ type: 'answer', sdp: 'v=0\r\na=answer\r\n' });

    const [path, init] = apiRaw.mock.calls[0];
    expect(path).toBe('/api/stream/7/whep');
    expect(init.method).toBe('POST');
    expect(init.headers['Content-Type']).toBe('application/sdp');
    expect(v.isOpen).toBe(true);
    expect(v.sessionId).toBe('/api/stream/whep/abc');
  });

  it('要求次碼流時真的向伺服器要次碼流', async () => {
    // 「挑了次碼流但其實還在拉主流」不會有任何錯誤：畫面照樣出來、只是首包慢二十幾秒，
    // 而操作員看到的是一格一直沒有畫面。所以這條路徑要釘住請求本身。
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/api/stream/whep/abc' }),
      text: async () => 'v=0',
    }));
    const v = viewer({ apiRaw });

    await expect(v.open(7, fakeVideo(), vi.fn(), { stream: 'sub' })).resolves.toBe(true);

    expect(apiRaw.mock.calls[0][0]).toBe('/api/stream/7/whep?stream=sub');
  });

  it('只要求 video 且是 recvonly', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/x' }),
      text: async () => 'v=0',
    }));
    await viewer({ apiRaw }).open(1, fakeVideo(), vi.fn());

    const [transceiver] = FakePeer.instances[0].transceivers;
    expect(transceiver.kind).toBe('video');
    // 伺服器的 answer 是 a=sendonly；瀏覽器這邊若寫成 sendonly，兩端都是送端，
    // Chrome 會以「Incompatible receive direction」拒絕 answer 而完全開不起來。
    expect(transceiver.direction).toBe('recvonly');
  });

  it('ICE 收集完成前不會送出 offer', async () => {
    const peer = FakePeer;
    let sentDuringGathering = false;
    class SlowPeer extends peer {
      constructor() {
        super();
        this.iceGatheringState = 'gathering';
      }

      async setLocalDescription(desc) {
        this.localDescription = desc;
      }
    }
    globalThis.RTCPeerConnection = SlowPeer;

    const apiRaw = vi.fn(async () => {
      sentDuringGathering = SlowPeer.instances[0].iceGatheringState === 'complete';
      return { ok: true, status: 201, headers: new Headers({ Location: '/x' }), text: async () => 'v=0' };
    });

    const opening = viewer({ apiRaw }).open(1, fakeVideo(), vi.fn());
    // 收集尚未完成時 apiRaw 不該被呼叫（這是整個測試的重點）。
    await new Promise((r) => setTimeout(r, 0));
    expect(apiRaw).not.toHaveBeenCalled();

    const instance = SlowPeer.instances[0];
    instance.iceGatheringState = 'complete';
    instance.handlers.icegatheringstatechange.forEach((fn) => fn());

    await opening;
    expect(sentDuringGathering).toBe(true);
  });

  it('429 顯示觀看上限而不是原始錯誤', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: false,
      status: 429,
      json: async () => ({ error: 'viewer limit reached' }),
    }));
    const notify = vi.fn();
    const v = viewer({ apiRaw });

    await expect(v.open(1, fakeVideo(), notify)).resolves.toBe(false);
    expect(notify).toHaveBeenLastCalledWith('state.liveBusy');
    expect(v.isOpen).toBe(false);
  });

  it('其他錯誤顯示伺服器訊息', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: false,
      status: 400,
      json: async () => ({ error: 'bad sdp' }),
    }));
    const notify = vi.fn();

    await viewer({ apiRaw }).open(1, fakeVideo(), notify);
    expect(notify).toHaveBeenLastCalledWith('bad sdp');
  });

  it('錯誤回應不是 JSON 時仍能顯示狀態碼', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: false,
      status: 502,
      json: async () => {
        throw new Error('not json');
      },
    }));
    const notify = vi.fn();

    await viewer({ apiRaw }).open(1, fakeVideo(), notify);
    expect(notify).toHaveBeenLastCalledWith('live -> 502');
  });

  it('失敗時關閉 peer 並清空畫面', async () => {
    const apiRaw = vi.fn(async () => ({ ok: false, status: 403, json: async () => ({ error: 'nope' }) }));
    const video = fakeVideo();

    await viewer({ apiRaw }).open(1, video, vi.fn());

    expect(FakePeer.instances[0].closed).toBe(true);
    expect(video.srcObject).toBeNull();
  });

  it('協商成功時只宣告已連線，不宣告畫面已就緒', async () => {
    // 現場回報「WEB 看不到畫面」而介面寫著「即時畫面已就緒」：ontrack 在
    // setRemoteDescription 當下就會觸發，此時一張 RTP 都還沒進來。把協商成功講成
    // 播放成功，等於讓「防火牆擋住 UDP」與「正常播放」顯示成同一句話。
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/x' }),
      text: async () => 'v=0',
    }));
    const notify = vi.fn();

    await viewer({ apiRaw }).open(1, fakeVideoEl(), notify);

    expect(notify).toHaveBeenLastCalledWith('state.liveNegotiated');
    expect(notify).not.toHaveBeenCalledWith('state.liveReady');
  });

  it('第一張影格到達後才宣告播放中', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/x' }),
      text: async () => 'v=0',
    }));
    const notify = vi.fn();
    const video = fakeVideoEl();

    await viewer({ apiRaw }).open(1, video, notify);
    video.frame();
    await new Promise((r) => setTimeout(r, 0));

    expect(notify).toHaveBeenLastCalledWith('state.liveReady');
  });

  it('逾時還沒有影格時說收不到影像', async () => {
    vi.useFakeTimers();
    try {
      const apiRaw = vi.fn(async () => ({
        ok: true,
        status: 201,
        headers: new Headers({ Location: '/x' }),
        text: async () => 'v=0',
      }));
      const notify = vi.fn();

      await viewer({ apiRaw }).open(1, fakeVideoEl(), notify);
      await vi.advanceTimersByTimeAsync(11000);

      expect(notify).toHaveBeenLastCalledWith('state.liveNoFrame');
    } finally {
      vi.useRealTimers();
    }
  });

  it('宣告收不到影像之後，遲到的第一張影格仍會把標籤改成播放中', async () => {
    // 現場的網路攝影機同時被好幾條 WebRTC 拉流時，第一張影格可以晚到 12 秒以上。
    // 只有一次看門狗的話，畫面已經在播而格子標籤永遠寫著「收不到影像」——
    // 操作員會去查一個不存在的故障（防火牆、線路）。
    vi.useFakeTimers();
    try {
      const apiRaw = vi.fn(async () => ({
        ok: true,
        status: 201,
        headers: new Headers({ Location: '/x' }),
        text: async () => 'v=0',
      }));
      const notify = vi.fn();
      const video = fakeVideoEl();

      await viewer({ apiRaw }).open(1, video, notify);
      await vi.advanceTimersByTimeAsync(11000);
      expect(notify).toHaveBeenLastCalledWith('state.liveNoFrame');

      // 影格迟到但連線還在：標籤必須改口。
      video.frame();
      await vi.advanceTimersByTimeAsync(1);

      expect(notify).toHaveBeenLastCalledWith('state.liveReady');
    } finally {
      vi.useRealTimers();
    }
  });

  it('停止之後舊連線不會再把狀態寫回畫面', async () => {
    // 停掉再開下一路時，前一路的看門狗若還活著，就會用上一路的結果蓋掉新畫面的狀態。
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/x' }),
      text: async () => 'v=0',
    }));
    const notify = vi.fn();
    const v = viewer({ apiRaw });
    const video = fakeVideoEl();

    await v.open(1, video, notify);
    await v.stop();
    // 舊連線的看門狗（10 秒後）與影格事件都還可能到達；兩者都必須被會話身分擋掉。
    video.frame();
    await new Promise((r) => setTimeout(r, 0));

    expect(notify).not.toHaveBeenCalledWith('state.liveReady');
    expect(notify).not.toHaveBeenCalledWith('state.liveNoFrame');
  });
});

/**
 * 「有沒有畫面」的判準是 <c>videoWidth &gt; 0</c>，不是「POST 回了 201」。
 */
describe('waitForFirstFrame', () => {
  it('已經有影格時立即回報', async () => {
    const video = fakeVideoEl();
    video.videoWidth = 1920;

    await expect(waitForFirstFrame(video, 50)).resolves.toBe(true);
  });

  it('loadeddata 到達且尺寸非零時回報成功', async () => {
    const video = fakeVideoEl();
    const waiting = waitForFirstFrame(video, 1000);
    video.frame(1280, 720);

    await expect(waiting).resolves.toBe(true);
  });

  it('只派發事件但沒有尺寸時不算有畫面', async () => {
    // 有些播放器會在還沒有任何影像時就派發 playing；光看事件名字會誤報成功。
    const video = fakeVideoEl();
    const waiting = waitForFirstFrame(video, 30);
    video.dispatch('playing');
    video.dispatch('resize');
    await expect(waiting).resolves.toBe(false);
  });

  it('逾時時回報失敗而不是卡住', async () => {
    await expect(waitForFirstFrame(fakeVideoEl(), 20)).resolves.toBe(false);
  });

  it('結束後移除事件監聽，避免洩漏', async () => {
    const video = fakeVideoEl();
    const waiting = waitForFirstFrame(video, 20);

    await waiting;

    expect(video.listenerCount()).toBe(0);
  });

  it('逾時設為 0 時只等影格，不會自己回報沒有影格', async () => {
    // 呼叫端用它在「已宣告收不到影像」之後繼續等那張遲到的影格。
    const video = fakeVideoEl();
    let settled = null;
    void waitForFirstFrame(video, 0).then((arrived) => { settled = arrived; });

    await new Promise((r) => setTimeout(r, 30));
    expect(settled).toBeNull();

    video.frame(1280, 720, 'loadeddata');
    await new Promise((r) => setTimeout(r, 0));
    expect(settled).toBe(true);
  });
});

describe('createLiveViewer.stop', () => {
  it('關閉 peer 並對 Location 發 DELETE', async () => {
    const apiRaw = vi.fn(async () => ({ ok: true, status: 201, headers: new Headers({ Location: '/api/stream/whep/abc' }), text: async () => 'v=0' }));
    const video = fakeVideo();
    const v = viewer({ apiRaw });
    await v.open(1, video, vi.fn());

    await expect(v.stop()).resolves.toBe(true);

    expect(FakePeer.instances[0].closed).toBe(true);
    expect(video.srcObject).toBeNull();
    const [path, init] = apiRaw.mock.calls[1];
    expect(path).toBe('/api/stream/whep/abc');
    expect(init.method).toBe('DELETE');
    expect(v.isOpen).toBe(false);
  });

  it('DELETE 帶 keepalive 才能在關分頁之後送達', async () => {
    // 少了 keepalive，pagehide 之後文件被丟掉、請求被取消，伺服器永遠不知道這個人走了。
    // 而那條會話不會自己逾時：媒體還在流，每個封包都會刷新它的活動時間——實測瀏覽器被砍掉
    // 之後 publisher 活了好幾分鐘，狀態端點回報 viewers=1。名額就是這樣被佔死的。
    const apiRaw = vi.fn(async () => ({ ok: true, status: 201, headers: new Headers({ Location: '/api/stream/whep/abc' }), text: async () => 'v=0' }));
    const v = viewer({ apiRaw });
    await v.open(1, fakeVideo(), vi.fn());

    await v.stop();

    expect(apiRaw.mock.calls[1][1].keepalive).toBe(true);
  });

  it('DELETE 失敗時仍然算已停止', async () => {
    const apiRaw = vi.fn(async (path, init) => {
      if (init?.method === 'DELETE') throw new Error('network down');
      return { ok: true, status: 201, headers: new Headers({ Location: '/x' }), text: async () => 'v=0' };
    });
    const v = viewer({ apiRaw });
    await v.open(1, fakeVideo(), vi.fn());

    await expect(v.stop()).resolves.toBe(true);
    expect(v.isOpen).toBe(false);
  });

  it('沒有連線時 stop 是安全的空操作', async () => {
    await expect(viewer().stop()).resolves.toBe(false);
  });

  it('close() 拋錯時不影響停止流程', async () => {
    const apiRaw = vi.fn(async () => ({ ok: true, status: 201, headers: new Headers({ Location: '/x' }), text: async () => 'v=0' }));
    const v = viewer({ apiRaw });
    await v.open(1, fakeVideo(), vi.fn());

    FakePeer.instances[0].close = () => {
      throw new Error('already closed');
    };

    await expect(v.stop()).resolves.toBe(true);
  });

  it('重新開啟前會先關掉舊連線', async () => {
    const apiRaw = vi.fn(async () => ({ ok: true, status: 201, headers: new Headers({ Location: '/x' }), text: async () => 'v=0' }));
    const v = viewer({ apiRaw });

    await v.open(1, fakeVideo(), vi.fn());
    await v.open(2, fakeVideo(), vi.fn());

    expect(FakePeer.instances).toHaveLength(2);
    expect(FakePeer.instances[0].closed).toBe(true);
    expect(FakePeer.instances[1].closed).toBe(false);
  });
});

/**
 * 部署限制提示（M244 §7）。
 *
 * 沒有 TURN 時，對稱 NAT／多層 NAT 的遠端瀏覽器一定連不上，而症狀只是播放器卡住、
 * 沒有任何錯誤。這裡守的是三件事：值要照實回報、問不到時不亂講、以及 TURN 有設定時
 * 不要多嘴。
 */
describe('部署狀態查詢', () => {
  const statusResponse = (body, ok = true) => ({
    ok,
    status: ok ? 200 : 500,
    json: async () => body,
  });

  it('照實回報伺服器說有沒有 TURN', async () => {
    const apiRaw = vi.fn(async () => statusResponse({
      turnConfigured: true,
      publicHostConfigured: false,
    }));

    await expect(fetchDeployment(1, apiRaw)).resolves.toEqual({
      turnConfigured: true,
      publicHostConfigured: false,
    });
    expect(apiRaw).toHaveBeenCalledWith('/api/stream/1/whep');
  });

  it('舊版伺服器沒有這兩個欄位時不猜', async () => {
    // 缺欄位時若當成 false，UI 就會對著已配好 TURN 的主機說「沒有 TURN」，
    // 操作員會去重設一個已經生效的設定。
    const apiRaw = vi.fn(async () => statusResponse({ viewers: 1 }));

    await expect(fetchDeployment(1, apiRaw)).resolves.toBeNull();
  });

  it('非 2xx 時不顯示任何限制提示', async () => {
    const apiRaw = vi.fn(async () => statusResponse({}, false));

    await expect(fetchDeployment(1, apiRaw)).resolves.toBeNull();
  });

  it('網路失敗時不顯示任何限制提示', async () => {
    const apiRaw = vi.fn(async () => {
      throw new Error('offline');
    });

    await expect(fetchDeployment(1, apiRaw)).resolves.toBeNull();
  });

  it('通道編號不合法時不去打伺服器', async () => {
    const apiRaw = vi.fn();

    await expect(fetchDeployment(0, apiRaw)).resolves.toBeNull();
    await expect(fetchDeployment(Number.NaN, apiRaw)).resolves.toBeNull();
    expect(apiRaw).not.toHaveBeenCalled();
  });
});