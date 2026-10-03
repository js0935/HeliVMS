import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const cssSource = readFileSync(fileURLToPath(new URL('./styles.css', import.meta.url)), 'utf8');
const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');

/**
 * 版面回歸測試。
 *
 * 這些斷言守的是「按鈕／欄位看得到」而不是外觀喜好。M244 之後這份 SPA 被拿來
 * 接真實資料（通道名稱長度不可控、警報有 7 欄、英文介面的欄名比中文長），
 * 之後遇到的回報都是「某個按鈕被遮住／某欄看不到」——症狀出現在 CSS，
 * 但沒有任何既有測試會紅，所以這裡把每一個具體成因釘住。
 */

/** 取出某個選擇器規則的宣告本體。 */
function rule(selector) {
  const at = cssSource.indexOf(`${selector} {`);
  expect(at, `styles.css 找不到 ${selector} 的規則`).toBeGreaterThan(-1);
  return cssSource.slice(at, cssSource.indexOf('}', at));
}

describe('long content is not silently truncated', () => {
  it('renders the full audit detail and provider config instead of a 40-char prefix', () => {
    // 這兩欄原本用 `.slice(0, 40)` 硬截，稽核與 OIDC/LDAP 的設定值因此看不到
    // 重複的欄位或參數，出了問題也無從比對。改成完整顯示 + title 提示。
    expect(appSource).not.toMatch(/\.slice\(0, 40\)/);
    expect(appSource).toMatch(/<td class="wrap" title="\$\{esc\(r\.detail \?\? ''\)\}">\$\{esc\(r\.detail \?\? ''\)\}/);
    expect(appSource).toMatch(/<td class="muted wrap" title="\$\{esc\(p\.config\)\}">\$\{esc\(p\.config\)\}/);
  });

  it('marks the two wrapping columns in the markup, not just in CSS', () => {
    // 只有 CSS 有 .wrap 而沒有元素用它，等於什麼都沒換行——這是那條規則最容易
    // 悄悄失效的方式（改樣式的人不會想到要回頭改 HTML）。
    expect(indexSource).toMatch(/<th scope="col" class="wrap" data-i18n="col\.detail"/);
    expect(indexSource).toMatch(/<th scope="col" class="wrap" data-i18n="nav\.settings"/);
  });
});

describe('horizontal overflow', () => {
  it('lets a panel scroll horizontally instead of clipping its table', () => {
    // 表格欄位一多（警報 7 欄）會撐破面板右緣；面板沒有捲動容器的話，
    // 多出來的部分直接被裁掉，使用者永遠看不到「動作」欄。
    expect(rule('.panel')).toMatch(/overflow-x:\s*auto/);
  });

  it('keeps table cells on one line so row heights stay stable across locales', () => {
    expect(rule('th, td')).toMatch(/white-space:\s*nowrap/);
  });

  it('still allows genuinely long cells to wrap inside the scroll container', () => {
    expect(cssSource).toMatch(/\.wrap \{ white-space: normal; \}/);
  });
});

describe('form controls are not squeezed or wrapped', () => {
  it('wraps form rows instead of overflowing them', () => {
    expect(rule('form')).toMatch(/flex-wrap:\s*wrap/);
  });

  it('gives flexible inputs a zero minimum so they can shrink below content width', () => {
    // flex:1 加上預設的 min-width:auto 會讓 input 拒絕縮小，於是整列溢出。
    expect(rule('form input')).toMatch(/min-width:\s*0/);
  });

  it('does not stretch intrinsically narrow inputs to fill the row', () => {
    // 日期與時間欄位被 flex:1 拉成等寬，會把旁邊的按鈕擠出可視範圍。
    const narrow = cssSource.slice(
      cssSource.indexOf('input[type="date"]'),
      cssSource.indexOf('input[type="password"]'),
    );
    expect(narrow).toMatch(/flex:\s*0 1 auto/);
    expect(narrow).toMatch(/field-sizing:\s*content/);
  });

  it.each(['date', 'time', 'datetime-local', 'number'])(
    'sizes %s inputs to their content',
    (type) => expect(cssSource).toMatch(new RegExp(`input\\[type="${type}"\\]`)),
  );

  it('never stretches a checkbox or radio to an equal share of the row', () => {
    // #sched-form 有 7 個週 checkbox；flex:1 會把每個拉成等分寬，
    // 勾選框和它的 label 文字因此被拉開距離，看起來像控制項錯位。
    const checkboxes = cssSource.slice(
      cssSource.indexOf('form input[type="checkbox"]'),
      cssSource.indexOf('input[type="date"]'),
    );
    expect(checkboxes).toMatch(/input\[type="radio"\]/);
    expect(checkboxes).toMatch(/flex:\s*0 0 auto/);
  });

  it('never wraps button labels', () => {
    // 中英文長度不同，摺行會讓同一列的按鈕高度不一致、看起來像被切半。
    expect(rule('button')).toMatch(/white-space:\s*nowrap/);
    expect(rule('button')).toMatch(/flex:\s*0 0 auto/);
  });

  it('wraps inline form rows so narrow screens stack instead of clipping', () => {
    expect(rule('.row-form')).toMatch(/flex-wrap:\s*wrap/);
  });
});

describe('topbar does not push controls off screen', () => {
  it('wraps to multiple rows on narrow viewports', () => {
    expect(rule('.topbar')).toMatch(/flex-wrap:\s*wrap/);
  });

  it('keeps status badges on one line', () => {
    // badge 文字含到期時間與通道名稱；摺行會撐高 topbar 並擠掉登出／語言選單。
    expect(rule('.badge')).toMatch(/white-space:\s*nowrap/);
  });
});

describe('fixed-size tiles do not silently truncate text', () => {
  it.each(['.map .pin', '.swtile'])('%s scrolls its label instead of clipping it', (selector) => {
    const body = rule(selector);
    expect(body).toMatch(/overflow-x:\s*auto/);
    expect(body).toMatch(/white-space:\s*nowrap/);
  });
});

describe('page never overflows the viewport horizontally', () => {
  it('lets the panel grid shrink below its 320px track minimum', () => {
    // minmax(320px, 1fr) 在 320px 寬的手機上會讓格線維持 320px，加上 .grid 自身的
    // padding 就超出畫面，整頁出現橫向捲動，錨點跳轉與 scroll-margin 全部失效。
    expect(rule('.grid')).toMatch(/minmax\(min\(320px, 100%\), 1fr\)/);
  });

  it('does not pin the inline nav to a single row', () => {
    // admin 區段連結一多，單列會被擠爆。
    expect(rule('.nav-inline')).toMatch(/flex-wrap:\s*wrap/);
  });
});

describe('video is never cropped', () => {
  it('letterboxes the stream rather than cutting off the top and bottom', () => {
    // 固定 16:9 的框配 4:3 的攝影機會把畫面上下切掉，這是「看不到畫面」
    // 最常見的成因。object-fit: contain 保留完整畫面。
    expect(rule('.player')).toMatch(/object-fit:\s*contain/);
  });
});