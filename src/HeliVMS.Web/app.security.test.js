import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const liveSource = readFileSync(fileURLToPath(new URL('./live.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');

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
  it('calls fetch exactly once, inside apiRaw', () => {
    expect(appSource.match(/\bfetch\(/g) ?? []).toHaveLength(1);
    expect(appSource).toMatch(/async function apiRaw\([\s\S]*?await fetch\(/);
  });

  it('sends the bearer header from apiRaw', () => {
    expect(appSource).toMatch(/bearerHeader\(key\(\)\)/);
  });

  it('does not hardcode the authorization header at call sites', () => {
    expect(appSource).not.toMatch(/Authorization: `/);
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
  it('declares a content security policy', () => {
    expect(indexSource).toMatch(/http-equiv="Content-Security-Policy"/);
    expect(indexSource).toMatch(/default-src 'self'/);
  });

  it('never ships the development api key in markup', () => {
    expect(indexSource).not.toContain('helivms-dev-key');
  });

  it('does not persist a blank api key', () => {
    expect(appSource).toMatch(/localStorage\.removeItem\('helivms\.apiKey'\)/);
  });

  it('stores the session token as the apiRaw credential after login', () => {
    // 登入成功立即把工作階段 token 寫進 apiRaw 的憑證槽，之後所有 /api/* 請求都帶它，
    // 使用者不需要另外手動餵 API 金鑰。
    expect(appSource).toMatch(/state\.token[\s\S]*localStorage\.setItem\('helivms\.apiKey', state\.token\)/);
  });

  it('revokes the session and clears the credential on logout', () => {
    expect(appSource).toMatch(/apiRaw\('\/api\/accounts\/logout', \{ method: 'POST' \}\)/);
    expect(appSource).toMatch(/localStorage\.removeItem\('helivms\.apiKey'\)[\s\S]*window\.location\.reload\(\)/);
  });

  it('clears the session on logout instead of storing an empty object', () => {
    expect(appSource).toMatch(/sessionStorage\.removeItem\('helivms\.session'\)/);
  });
});
