import { beforeEach, describe, expect, it, vi } from 'vitest';
import { DEFAULT_RETRY_DELAYS, createAlertSocket } from './alerts.js';

/**
 * 告警 WebSocket 重連。
 *
 * 守的是「伺服器重啟之後，這個頁面還會不會回來收告警」。
 * 原本的 onclose 只改了一行顯示文字就結束，所以任何一次斷線——伺服器重開機、
 * 網路閃斷、NAT 逾時——都會讓這個頁面永久靜默，只剩一個 "WS off" 的小字。
 * 沒有錯誤、沒有提示，復原方式是使用者剛好注意到並手動重新整理。
 */

/** 可手動觸發事件的假 socket。 */
class FakeSocket {
  static created = [];

  constructor(url) {
    this.url = url;
    this.closed = false;
    FakeSocket.created.push(this);
  }

  open() {
    this.onopen?.();
  }

  deliver(data) {
    this.onmessage?.({ data });
  }

  /** 伺服器端關閉／連線被reset。 */
  drop() {
    this.onclose?.({});
  }

  close() {
    this.closed = true;
  }
}

// 不重置的話每個測試都會看到前面測試留下的 socket，`created[0]` 指向別人的連線，
// 失敗訊息會指向完全不相干的地方。
beforeEach(() => {
  FakeSocket.created = [];
});

/** 建立被測 socket，並回傳可控的計時器佇列。 */
function setup(overrides = {}) {
  const timers = [];
  const messages = [];
  const statuses = [];
  const socket = createAlertSocket({
    url: () => 'ws://host/api/alerts/ws?key=k1',
    createSocket: (url) => new FakeSocket(url),
    setTimeout: (fn, ms) => {
      timers.push({ fn, ms });
      return timers.length;
    },
    retryDelays: [10, 20, 30],
    onMessage: (data) => messages.push(data),
    onStatus: (s) => statuses.push(s),
    ...overrides,
  });
  return { socket, timers, messages, statuses };
}

describe('告警 WebSocket 重連', () => {
  it('connect 之後有一條連線，且狀態為上線', () => {
    const { socket, statuses } = setup();

    socket.connect();
    FakeSocket.created[0].open();

    expect(FakeSocket.created).toHaveLength(1);
    expect(statuses.at(-1)).toEqual({ online: true, attempt: 0 });
  });

  it('收到訊息會轉發出去', () => {
    const { socket, messages } = setup();

    socket.connect();
    FakeSocket.created[0].open();
    FakeSocket.created[0].deliver('{"kind":"alarm.new"}');

    expect(messages).toEqual(['{"kind":"alarm.new"}']);
  });

  /**
   * 斷線後必須自己接回來。這是整組測試存在的原因：現場症狀是
   * 「伺服器重開機之後主控台就再也不跳告警了」。
   */
  it('連線被關閉後會自動重連', () => {
    const { socket, timers } = setup();

    socket.connect();
    FakeSocket.created[0].drop();

    expect(timers).toHaveLength(1);
    timers[0].fn();
    expect(FakeSocket.created).toHaveLength(2);
  });

  it('退避會逐次變長', () => {
    const { socket, timers } = setup();

    socket.connect();
    FakeSocket.created[0].drop();
    timers[0].fn();
    FakeSocket.created[1].drop();
    timers[1].fn();
    FakeSocket.created[2].drop();

    expect(timers.map((t) => t.ms)).toEqual([10, 20, 30]);
  });

  it('退避不會無限成長，停在最後一格', () => {
    const { socket, timers } = setup({ retryDelays: [10, 20] });

    socket.connect();
    FakeSocket.created.at(-1).drop();
    for (let i = 0; i < 8; i++) {
      timers.at(-1).fn();
      FakeSocket.created.at(-1).drop();
    }

    // 第一格是 10，之後不論斷幾次都停在最後一格的 20。
    expect(timers[0].ms).toBe(10);
    expect(timers.slice(1).every((t) => t.ms === 20)).toBe(true);
    expect(timers).toHaveLength(9);
  });

  /** 早上斷過一次不該讓之後每次失敗都從最長的延遲開始等。 */
  it('重連成功後退避歸零', () => {
    const { socket, timers } = setup();

    socket.connect();
    FakeSocket.created[0].drop();
    timers[0].fn();
    FakeSocket.created[1].drop();
    timers[1].fn();
    FakeSocket.created[2].open();
    FakeSocket.created[2].drop();

    expect(timers.at(-1).ms).toBe(10);
  });

  it('close 之後不再重連', () => {
    const { socket } = setup();

    socket.connect();
    socket.close();
    FakeSocket.created[0].drop();

    expect(FakeSocket.created).toHaveLength(1);
  });

  /** 頁面離開後還在背景重連會是資源洩漏，而且 log 會一直有噪音。 */
  it('close 之後不再排新的重連計時器', () => {
    const { socket, timers } = setup();

    socket.connect();
    socket.close();

    expect(timers).toHaveLength(0);
  });

  /**
   * 重連必須重新求值 url。登入 token 是會換發的，用舊的 url 重連會一直拿 401，
   * 結果就是「明明重新整理就好了，但自動重連永遠連不上」。
   */
  it('重連時使用最新取得的網址', () => {
    let key = 'k1';
    const { socket, timers } = setup({ url: () => `ws://host/ws?key=${key}` });

    socket.connect();
    key = 'k2';
    FakeSocket.created[0].drop();
    timers[0].fn();

    expect(FakeSocket.created[1].url).toBe('ws://host/ws?key=k2');
  });

  /** 建立 socket 就失敗（例如完全沒網路）也要重連，否則一次例外就永久靜默。 */
  it('建立連線當下失敗也會重試', () => {
    let failNext = true;
    const { socket, timers } = setup({
      createSocket: (url) => {
        if (failNext) {
          failNext = false;
          throw new Error('no network');
        }
        return new FakeSocket(url);
      },
    });

    socket.connect();
    expect(timers).toHaveLength(1);

    timers[0].fn();
    expect(FakeSocket.created).toHaveLength(1);
  });

  /** onerror 與 onclose 會同時發；只重連一次才不會變成斷線時自我複製。 */
  it('同一條連線不會排入兩次重連', () => {
    const { socket, timers } = setup();

    socket.connect();
    const s = FakeSocket.created[0];
    s.onerror?.({});
    s.drop();

    expect(timers).toHaveLength(1);
  });

  it('預設退避最後一格是 30 秒', () => {
    expect(DEFAULT_RETRY_DELAYS.at(-1)).toBe(30000);
  });

  it('連線開著時 isOpen 為 true，斷線後為 false', () => {
    const { socket } = setup();

    socket.connect();
    expect(socket.isOpen()).toBe(true);

    FakeSocket.created[0].drop();
    expect(socket.isOpen()).toBe(false);
  });

  it('close 不會讓已經關閉的 socket 再拋一次', () => {
    const { socket } = setup();

    socket.connect();
    const s = FakeSocket.created[0];
    s.closed = true;
    vi.spyOn(s, 'close').mockImplementation(() => {
      throw new Error('already closed');
    });

    expect(() => socket.close()).not.toThrow();
  });
});