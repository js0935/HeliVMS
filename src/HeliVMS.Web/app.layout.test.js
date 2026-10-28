import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const cssSource = readFileSync(fileURLToPath(new URL('./styles.css', import.meta.url)), 'utf8');
const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');

/**
 * 版面回歸測試。
 *
 * 這些斷言守的是「按鈕／欄位看得到」而不是外觀喜好。這份 SPA 被拿來接真實資料
 * （通道名稱長度不可控、事件搜尋 5 欄、英文介面的欄名比中文長），回報都是
 * 「某個按鈕被遮住／某欄看不到」——症狀出現在 CSS，但沒有既有測試會紅，
 * 所以這裡把每一個具體成因釘住。
 */

/** 取出某個選擇器規則的宣告本體。 */
function rule(selector) {
  const at = cssSource.indexOf(`${selector} {`);
  expect(at, `styles.css 找不到 ${selector} 的規則`).toBeGreaterThan(-1);
  return cssSource.slice(at, cssSource.indexOf('}', at));
}

describe('five feature tabs', () => {
  it('wraps the tab strip so five labels do not overflow on narrow screens', () => {
    // 即時／回放／截圖／PTZ／事件五個頁籤在英文介面比中文長；單列會被擠爆
    // 並把後面的頁籤推出畫面，操作員就再也點不到 PTZ。
    expect(rule('.tabs')).toMatch(/flex-wrap:\s*wrap/);
  });

  it('never wraps a tab label', () => {
    // 頁籤文字摺行會讓同一列的頁籤高度不一致，看起來像被切半。
    expect(rule('.tab')).toMatch(/white-space:\s*nowrap/);
  });

  it('hides inactive panels via the hidden attribute, not visibility', () => {
    // app.js 用 `panel.hidden = ...`；若 CSS 沒有對應規則，display 預設值會蓋掉
    // hidden，五個面板會同時顯示。
    expect(cssSource).toMatch(/\.panel\[hidden\] \{ display: none; \}/);
    expect(appSource).toMatch(/panel\.hidden = panel\.dataset\.panel !== name/);
  });
});

describe('horizontal overflow', () => {
  it('lets a panel scroll horizontally instead of clipping its table', () => {
    // 事件搜尋有 5 欄，內容一長就撐破面板右緣；面板沒有捲動容器的話，
    // 多出來的部分直接被裁掉，使用者永遠看不到右側的狀態欄。
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

describe('PTZ pad stays usable', () => {
  it('lays the direction pad out as a grid', () => {
    // 九宮格靠 grid 排版；退成 inline 會讓方向鍵全部擠成一團。
    expect(rule('.ptz-pad')).toMatch(/display:\s*grid/);
  });

  it('keeps direction buttons on one line', () => {
    expect(rule('.ptz-btn')).toMatch(/white-space:\s*nowrap/);
  });

  it('ships all nine movement directions plus stop in the markup', () => {
    const moves = indexSource.match(/data-move="[^"]+"/g) ?? [];
    // 8 個方向 + 2 個變焦 = 10 個 data-move 按鈕；少任何一個都代表某一向按不到。
    expect(moves.length).toBe(10);
    expect(indexSource).toMatch(/id="ptz-stop"/);
  });
});

describe('page never overflows the viewport horizontally', () => {
  it('lets the panel grid shrink below its 320px track minimum', () => {
    // minmax(320px, 1fr) 在 320px 寬的手機上會讓格線維持 320px，加上 .grid 自身的
    // padding 就超出畫面，整頁出現橫向捲動。
    expect(rule('.grid')).toMatch(/minmax\(min\(320px, 100%\), 1fr\)/);
  });
});

describe('video is never cropped', () => {
  it('letterboxes the stream rather than cutting off the top and bottom', () => {
    // 固定 16:9 的框配 4:3 的攝影機會把畫面上下切掉，這是「看不到畫面」
    // 最常見的成因。object-fit: contain 保留完整畫面。
    expect(rule('.player')).toMatch(/object-fit:\s*contain/);
  });
});

describe('playback clock stays readable and still', () => {
  it('ships a clock row under the playback video', () => {
    // 時鐘是回放時唯一的「這一刻是什麼時候錄的」依據；規則與元素任何一邊
    // 消失都會讓它退回無樣式的裸文字（或整個不出現）。
    expect(cssSource).toMatch(/\.play-clock \{/);
    expect(indexSource).toMatch(/id="play-clock"/);
  });

  it('keeps digit widths fixed so per-second updates do not jitter the row', () => {
    // 每秒換一次字串；比例數字（1 vs 0）寬度不同會讓整列左右抖動。
    expect(rule('.play-clock')).toMatch(/font-variant-numeric:\s*tabular-nums/);
  });

  it('reserves the row height before the first timestamp arrives', () => {
    // 空 div 高度為 0，時鐘出現時下方說明列會往下跳；min-height 先佔好位。
    expect(rule('.play-clock')).toMatch(/min-height:/);
    expect(rule('.play-clock')).toMatch(/white-space:\s*nowrap/);
  });
});

describe('multi-split live grid', () => {
  it('lays the split cells out in a grid', () => {
    expect(rule('.live-grid')).toMatch(/display:\s*grid/);
  });

  it('lets a cell shrink below its content so 5x5 does not overflow', () => {
    // 格線用 minmax(0, 1fr)（由 JS 設在 inline style）；單格若沒有 min-width:0，
    // video 的最小內容寬會把格子撐開，5x5 就會讓面板出現橫向捲動。
    expect(rule('.live-cell')).toMatch(/min-width:\s*0/);
  });

  it('removes the single-player width cap inside a cell', () => {
    // .player 的 max-width: 720px 是給單螢幕用的；留在格子裡會讓大電視牆
    // 每格都縮在左邊 720px、右半空白。
    expect(rule('.live-cell .player')).toMatch(/max-width:\s*none/);
  });
});
