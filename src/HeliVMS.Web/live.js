/**
 * M244 WebRTC 即時監看：用原生 RTCPeerConnection 走 WHEP。
 *
 * 刻意不引入任何 WHEP 播放器函式庫（WhepClient 之類）。WHEP 就是「POST offer →
 * 拿 answer → 設到 PeerConnection」，兩次 fetch 加三行設定；引入函式庫換來的只是
 * 幾 KB 程式碼，卻多一份要追漏洞、會被 scanner 擋下、且與 repo「零外部相依」的原則
 * 相衝突的相依。參見 docs/milestones/M244-webrtc-live.md §4。
 *
 * 伺服器不做 trickle ICE（沒有 PATCH 端點），所以這裡要**等 ICE 收集完成**
 * 再送出 offer；少了這個等待，answer 裡會沒有候選而永遠連不上。
 */

/** 送出 offer 前最多等 ICE 收集多久；與伺服器的 PublisherStartTimeout 無關，這裡只管自己的候選。 */
const ICE_GATHER_TIMEOUT_MS = 3000;

/**
 * 建立一個即時監看控制器。
 *
 * 傳輸層（apiRaw／api）與翻譯（t）都由呼叫端注入，而不是在模組裡直接抓全域變數。
 * 這是為了能在 vitest 裡用假 PeerConnection／假 fetch 測試協商流程——
 * app.js 裡的 apiRaw 綁著真實的 API key 與 fetch，若 live.js 直接引用它，
 * 這條路徑就只能靠人工點擊驗證，而 M244 最脆弱的正是協商這一步。
 *
 * @param {{apiRaw: Function, t: Function}} deps
 */
export function createLiveViewer({ apiRaw, t }) {
  let session = null;

  /** 送出 offer 並取回 answer。 */
  async function negotiate(channelId, peer) {
    const offer = await peer.createOffer();
    await peer.setLocalDescription(offer);

    // 不做 trickle 就必須自己等候選收齊，否則 answer 裡不會有候選而永遠連不上。
    if (peer.iceGatheringState !== 'complete') {
      await waitForIceGathering(peer);
    }

    const response = await apiRaw(`/api/stream/${channelId}/whep`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/sdp' },
      body: peer.localDescription.sdp,
    });

    if (!response.ok) {
      // 429 是「有人看太多，請稍後」、403 是授權不足，兩者對使用者的意義完全不同，
      // 所以把伺服器給的訊息保留下來，而不是只顯示狀態碼。
      const info = await response.json().catch(() => null);
      const error = new Error(info?.error ?? `live -> ${response.status}`);
      error.status = response.status;
      throw error;
    }

    await peer.setRemoteDescription({ type: 'answer', sdp: await response.text() });

    // WHEP 規定會話的識別在 Location；沒有它就無法在關閉時釋放伺服器端資源。
    return { location: response.headers.get('Location') ?? '' };
  }

  /** 關閉目前的即時連線（若有的話）。 */
  async function stop() {
    const current = session;
    if (!current) return false;

    // 先清掉引用再收尾：DELETE 失敗時也不能讓「停止」看起來沒反應。
    session = null;
    current.video.srcObject = null;

    try {
      current.peer.close();
    } catch (err) {
      // 已關閉的 PeerConnection 再關一次不該讓 UI 卡住。
    }

    // 一定要通知伺服器：沒有 DELETE，被遺忘的會話會佔住該通道的觀看名額直到逾時
    // 回收（預設 30 秒）。使用者的「停止」按鈕必須是立即釋放。
    try {
      if (current.location) await apiRaw(current.location, { method: 'DELETE' });
    } catch (err) {
      // 伺服器最終仍會逾時回收；這裡不該讓使用者看到錯誤。
    }

    return true;
  }

  /**
   * 開啟某通道的即時畫面。
   * @param {number} channelId 通道編號。
   * @param {HTMLVideoElement} video 顯示畫面的元素。
   * @param {(text: string) => void} notify 把狀態訊息寫進 live region。
   */
  async function open(channelId, video, notify) {
    if (!Number.isFinite(channelId) || channelId <= 0) {
      notify(t('state.channelPositive'));
      return false;
    }

    // 這個瀏覽器可能完全沒有 WebRTC（舊 Safari、功能被停用）。要在開啟前講清楚，
    // 而不是讓按下按鈕之後完全沒有反應。
    if (typeof RTCPeerConnection !== 'function') {
      notify(t('state.webrtcUnsupported'));
      return false;
    }

    await stop();

    const peer = new RTCPeerConnection({ iceServers: [] });
    // 伺服器不送音軌；這裡也不要求音訊，避免多開一條 m-line 造成協商失敗。
    peer.addTransceiver('video', { direction: 'sendonly' });
    video.srcObject = new MediaStream();

    peer.ontrack = (ev) => {
      if (ev.streams[0]) video.srcObject = ev.streams[0];
    };

    peer.onconnectionstatechange = () => {
      if (peer.connectionState === 'failed') {
        notify(t('state.liveFailed'));
        void stop();
      }
    };

    notify(t('state.liveConnecting'));

    try {
      const { location } = await negotiate(channelId, peer);
      session = { peer, location, video };
      notify(t('state.liveReady'));
      return true;
    } catch (err) {
      try {
        peer.close();
      } catch (closeErr) {
        // 已關閉。
      }

      video.srcObject = null;

      // 429 值得明確提示「已達觀看上限」而不是乾脆不說——使用者會自己去再開一個分頁。
      notify(err.status === 429 ? t('state.liveBusy') : (err.message ?? t('state.failed')));
      return false;
    }
}

  return {
    open,
    stop,
    get isOpen() {
      return session !== null;
    },
    get sessionId() {
      return session?.location ?? null;
    },
  };
}

/**
 * 問伺服器這台主機的 ICE 佈署狀態。
 *
 * TURN 與 PUBLIC_HOST 都是環境變數，操作員在畫面上看不到，卻是「遠端連不上」
 * 最常見的原因：沒有 TURN 時，對稱 NAT 一定連不回來，而症狀只是播放器卡住，
 * 沒有任何錯誤。M244 §7 要求這個限制在 UI 明說，所以要由伺服器據實回答。
 *
 * 回 `null` 表示問不到（舊版伺服器、網路失敗）。此時**不**顯示任何警告：
 * 寧可少一句提醒，也不要在沒有證據時對操作員講一個可能是錯的結論。
 *
 * @param {number} channelId 通道編號；狀態端點掛在通道上。
 * @returns {Promise<{turnConfigured: boolean, publicHostConfigured: boolean}|null>}
 */
export async function fetchDeployment(channelId, apiRaw) {
  if (!Number.isFinite(channelId) || channelId <= 0) return null;

  try {
    const response = await apiRaw(`/api/stream/${channelId}/whep`);
    if (!response.ok) return null;

    const info = await response.json().catch(() => null);

    // 兩個欄位缺一個就當作問不到：舊版伺服器沒有這兩個值，
    // 用 undefined 當 false 會讓 UI 對著有 TURN 的主機說「沒有 TURN」。
    if (typeof info?.turnConfigured !== 'boolean'
      || typeof info?.publicHostConfigured !== 'boolean') {
      return null;
    }

    return {
      turnConfigured: info.turnConfigured,
      publicHostConfigured: info.publicHostConfigured,
    };
  } catch (err) {
    return null;
  }
}

/** 等 ICE 收集完成，最多等 {@link ICE_GATHER_TIMEOUT_MS}；已完成時立即返回。 */
export function waitForIceGathering(peer, timeoutMs = ICE_GATHER_TIMEOUT_MS) {
  return new Promise((resolve) => {
    if (peer.iceGatheringState === 'complete') {
      resolve();
      return;
    }

    let settled = false;
    const finish = () => {
      if (settled) return;
      settled = true;
      clearTimeout(timer);
      peer.removeEventListener?.('icegatheringstatechange', onChange);
      resolve();
    };

    function onChange() {
      if (peer.iceGatheringState === 'complete') finish();
    }

    // 逾時也往下走：某些網路環境永遠收不齊候選，與其卡住整個畫面，
    // 不如讓伺服器用自己的 STUN 設定產生候選。
    const timer = setTimeout(finish, timeoutMs);
    peer.addEventListener?.('icegatheringstatechange', onChange);
  });
}
