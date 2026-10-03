import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createLiveViewer, fetchDeployment, waitForIceGathering } from './live.js';

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

  it('只要求 video 且是 sendonly', async () => {
    const apiRaw = vi.fn(async () => ({
      ok: true,
      status: 201,
      headers: new Headers({ Location: '/x' }),
      text: async () => 'v=0',
    }));
    await viewer({ apiRaw }).open(1, fakeVideo(), vi.fn());

    const [transceiver] = FakePeer.instances[0].transceivers;
    expect(transceiver.kind).toBe('video');
    expect(transceiver.direction).toBe('sendonly');
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