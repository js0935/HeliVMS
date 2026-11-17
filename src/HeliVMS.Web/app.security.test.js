import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const liveSource = readFileSync(fileURLToPath(new URL('./live.js', import.meta.url)), 'utf8');
const authSource = readFileSync(fileURLToPath(new URL('./auth.js', import.meta.url)), 'utf8');
const loginSource = readFileSync(fileURLToPath(new URL('./login.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');
const loginHtml = readFileSync(fileURLToPath(new URL('./login.html', import.meta.url)), 'utf8');

/** Balanced scan of `${...}` interpolations; nested templates and braces are tracked. */
function interpolations(source) {
  const found = [];
  for (let i = 0; i < source.length; i++) {
    if (source[i] !== '$' || source[i + 1] !== '{') continue;
    let depth = 1;
    let j = i + 2;
    let backticks = 0;
    for (; j < source.length && depth > 0; j++) {
      const c = source[j];
      if (c === '`') backticks++;
      else if (c === '}' && backticks === 0) depth--;
      else if (c === '{') depth++;
      else if (c === "'" || c === '"') {
        const quote = c;
        for (j++; j < source.length && source[j] !== quote; j++) {
          if (source[j] === '\\') j++;
        }
      }
    }
    found.push(source.slice(i + 2, j - 1).trim());
    i = j - 1;
  }
  return found;
}

/** Statements that write HTML, from the assignment up to the joining `.join(` call. */
function htmlAssignments(source) {
  const out = [];
  const marker = '.innerHTML =';
  for (let i = source.indexOf(marker); i >= 0; i = source.indexOf(marker, i + 1)) {
    let depth = 0;
    let backticks = 0;
    let j = i + marker.length;
    for (; j < source.length; j++) {
      const c = source[j];
      if (c === '`') backticks++;
      else if (c === '(') depth++;
      else if (c === ')') {
        depth--;
        if (depth === 0 && source.slice(j - 8, j - 2) === '.join(') {
          out.push(source.slice(i, j + 1));
          break;
        }
      }
    }
  }
  return out;
}

describe('single authenticated transport', () => {
  it('calls fetch exactly once, inside the shared apiRaw in auth.js', () => {
    // login.html 與 index.html 現在是兩頁，但憑證只有一份 transport。任一方自己開一個 fetch
    // 就會漏 bearer header，症狀是「登入成功卻每一支 API 都 401」。
    expect(authSource.match(/\bfetch\(/g) ?? []).toHaveLength(1);
    expect(authSource).toMatch(/export async function apiRaw\([\s\S]*?await fetch\(/);
    expect(appSource).not.toMatch(/\bfetch\(/);
    expect(loginSource).not.toMatch(/\bfetch\(/);
    expect(liveSource).not.toMatch(/\bfetch\(/);
  });

  it('sends the bearer header from the shared apiRaw', () => {
    expect(authSource).toMatch(/bearerHeader\(key\(\)\)/);
  });

  it('makes both pages import the shared transport', () => {
    expect(appSource).toMatch(/from '\.\/auth\.js'/);
    expect(loginSource).toMatch(/from '\.\/auth\.js'/);
  });

  it('does not hardcode the authorization header at call sites', () => {
    for (const source of [authSource, appSource, loginSource]) {
      expect(source).not.toMatch(/Authorization: `/);
    }
  });

  it('keeps the live-stream viewer on the injected transport', () => {
    // live.js 的 WHEP 是唯一帶 method 與 DELETE 的請求。它必須使用 app.js 注入的 apiRaw
    // （唯一 fetch 出口），否則會繞過 bearer header 而 401，也讓「只有一個 fetch」失效。
    expect(liveSource).not.toMatch(/\bfetch\(/);
    expect(liveSource).toMatch(/createLiveViewer\(\{\s*apiRaw/);
  });
});

describe('escaped rendering', () => {
  it('has no innerHTML sink built from unchecked values', () => {
    const unsafe = htmlAssignments(appSource).filter((block) => {
      if (block.includes(".innerHTML = ''")) return false;
      // tx() 是「譯文並跳脫」，與 esc() 同級：M243 起 UI 字串一律經由它進入 HTML，
      // 它的跳脫行為另有一條測試釘住（tx_escapesInterpolatedParams）。
      return interpolations(block).some((expr) => !/^(esc|escPct|tx|Number)\(/.test(expr));
    });
    expect(unsafe).toEqual([]);
  });

  it('covers every table renderer, so the rule cannot pass by rendering nothing', () => {
    // 事件表 / 子節點選單 / 頻道 datalist / 本機頻道快選 / PTZ 碼流 / PTZ 預設點：六處 inline 模板。
    // 門檻刻意設在 3 而不是 5：少一兩處仍會被抓出來（斷言會失敗），但這裡允許
    // 小幅重構而不必同步改這個魔術數字。
    expect(htmlAssignments(appSource).length).toBeGreaterThan(3);
  });

  it('still clears containers without escaping', () => {
    expect(appSource).toMatch(/\.innerHTML = ''/);
  });
});

describe('shell hardening', () => {
  it('declares a content security policy on both pages', () => {
    for (const source of [indexSource, loginHtml]) {
      expect(source).toMatch(/http-equiv="Content-Security-Policy"/);
      expect(source).toMatch(/default-src 'self'/);
    }
  });

  it('allows blob media so the HLS playback MediaSource can load', () => {
    // 遠程回放把 MediaSource 掛在 blob: URL 上（app.js attachPlaylist）。CSP 沒有 media-src
    // 時會退回 default-src 'self'，blob: 當場被擋掉：video.error=4、訊息永遠停在「載入中」，
    // 而即時監看（WebRTC 用 srcObject，不走 URL）完全正常。也就是「同一個頁面，監看有畫面、
    // 回放黑屏」而且主控台只有一行 CSP 違規——這正是最難歸因的一種「WEB 看不到畫面」。
    const policy = indexSource.match(/http-equiv="Content-Security-Policy"[\s\S]*?content="([^"]+)"/)?.[1] ?? '';
    const directives = policy.split(';').map((part) => part.trim().split(/\s+/));
    const mediaSrc = directives.find((d) => d[0] === 'media-src');
    expect(mediaSrc, 'CSP 需要明確的 media-src，否則 blob: 會繼承 default-src 而被擋').toBeDefined();
    expect(mediaSrc).toContain("'self'");
    expect(mediaSrc).toContain('blob:');
  });

  it('never ships the development api key in markup', () => {
    expect(indexSource).not.toContain('helivms-dev-key');
    expect(loginHtml).not.toContain('helivms-dev-key');
  });

  it('does not persist a blank api key', () => {
    expect(authSource).toMatch(/localStorage\.removeItem\(KEY_STORAGE\)/);
  });

  it('stores the session token as the shared transport credential after login', () => {
    // 登入頁拿到 token 後寫進共用憑證槽，主介面所有 /api/* 請求都帶它，
    // 使用者不需要另外手動餵 API 金鑰。
    expect(loginSource).toMatch(/setApiKey\(state\.token\)/);
    expect(authSource).toMatch(/localStorage\.setItem\(KEY_STORAGE,/);
  });

  it('revokes the session and returns to the standalone login page on logout', () => {
    expect(appSource).toMatch(/apiRaw\('\/api\/accounts\/logout', \{ method: 'POST' \}\)/);
    expect(appSource).toMatch(/clearApiKey\(\)[\s\S]*window\.location\.replace\('login\.html'\)/);
  });

  it('clears the session on logout instead of storing an empty object', () => {
    expect(authSource).toMatch(/sessionStorage\.removeItem\(SESSION_STORAGE\)/);
  });

  it('gives the standalone login page its own module script, not the main app', () => {
    expect(loginHtml).toMatch(/<script type="module" src="\.\/login\.js"><\/script>/);
    expect(loginHtml).not.toMatch(/src="\.\/app\.js"/);
    expect(indexSource).toMatch(/<script type="module" src="\.\/app\.js"><\/script>/);
    expect(indexSource).not.toMatch(/src="\.\/login\.js"/);
  });
});
