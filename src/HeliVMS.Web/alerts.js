/**
 * 即時告警 WebSocket，含自動重連。
 *
 * 這裡原本是一段 `connectLive()`：建立連線、onclose 只把畫面文字改成 "WS off"。
 * 沒有重連等於「伺服器一重啟、網路一斷、任何原因導致 socket 關閉之後，
 * 這個頁面就永遠收不到告警了」。對 7x24 的監控主控台來說這是最糟的失效：
 * 畫面上只有一個 "WS off" 的小字，沒有錯誤、沒有提示，而唯一的復原方式是
 * 使用者剛好注意到並手動重新整理頁面。
 *
 * 重連採指數退避（1s 起、上限 30s）：斷線通常是一起發生 hundreds 個用戶端的
 * （伺服器重開機），同時立刻重連會讓剛好恢復的服務又被一次洪水打垮。
 *
 * 依賴（url、createSocket、setTimeout）都由呼叫端注入，這樣這段重連邏輯
 * 可以在測試裡用假物件驗證，而不必真的開一條 socket 或真的等 30 秒。
 */

/** 預設退避毫秒序列，超過最後一項就固定用它。 */
export const DEFAULT_RETRY_DELAYS = [1000, 2000, 4000, 8000, 15000, 30000];

/**
 * 建立一個會自動重連的告警連線。
 *
 * @param {object} options
 * @param {() => string} options.url 每次嘗試（含重連）都重新求值，讓換發的 token 生效。
 * @param {(msg: unknown) => void} [options.onMessage] 收到可解析的訊息時呼叫。
 * @param {(state: {online: boolean, attempt: number}) => void} [options.onStatus] 連線狀態變化。
 * @param {(url: string) => object} [options.createSocket] 測試注入用；預設 `new WebSocket(url)`。
 * @param {(fn: () => void, ms: number) => any} [options.setTimeout] 測試注入用。
 * @param {number[]} [options.retryDelays] 測試可用短延遲。
 * @returns {{connect: () => void, close: () => void, isOpen: () => boolean}}
 */
export function createAlertSocket(options) {
  const {
    url,
    onMessage,
    onStatus,
    createSocket = (target) => new WebSocket(target),
    setTimeout: schedule = (fn, ms) => setTimeout(fn, ms),
    retryDelays = DEFAULT_RETRY_DELAYS,
  } = options;

  let socket = null;
  let attempt = 0;
  let retryTimer = null;
  let stopped = false;

  const clearRetry = () => {
    if (retryTimer !== null) {
      clearTimeout(retryTimer);
      retryTimer = null;
    }
  };

  const open = () => {
    if (stopped) return;
    clearRetry();

    let next;
    try {
      // url 每次重連都重新求值：登入 token 可能已經換發，用舊的會一直拿到 401。
      next = createSocket(url());
    } catch {
      // 建立 socket 本身就失敗（例如網路完全斷了）同樣要重連，否則一次例外就結束了。
      scheduleRetry();
      return;
    }
    socket = next;

    next.onopen = () => {
      // 連上就把退避歸零，否則早上斷過一次之後每次失敗都從 1 分鐘開始等。
      attempt = 0;
      onStatus?.({ online: true, attempt: 0 });
    };
    next.onmessage = (ev) => onMessage?.(ev.data);
    next.onclose = () => {
      socket = null;
      onStatus?.({ online: false, attempt });
      // 只在 onclose 重連，不在 onerror 重連：兩者都會發時會排兩次重連，
      // 最後長出一個每次斷線都把自己加倍的金字塔。
      scheduleRetry();
    };
  };

  const scheduleRetry = () => {
    if (stopped) return;
    const delays = retryDelays.length > 0 ? retryDelays : DEFAULT_RETRY_DELAYS;
    const delay = delays[Math.min(attempt, delays.length - 1)];
    attempt += 1;
    clearRetry();
    retryTimer = schedule(() => {
      retryTimer = null;
      open();
    }, delay);
  };

  return {
    connect() {
      stopped = false;
      open();
    },
    /** 永久停止：頁面卸載、登出時呼叫，否則離開頁面後仍在背景重連。 */
    close() {
      stopped = true;
      clearRetry();
      const current = socket;
      socket = null;
      if (current) {
        current.onclose = null;
        current.onopen = null;
        current.onmessage = null;
        try {
          current.close();
        } catch {
          // 已經關掉的 socket 再關一次不會有問題，不該因此讓呼叫端出錯。
        }
      }
    },
    isOpen: () => socket !== null,
  };
}