import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const cssSource = readFileSync(fileURLToPath(new URL('./styles.css', import.meta.url)), 'utf8');
const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');
const loginHtml = readFileSync(fileURLToPath(new URL('./login.html', import.meta.url)), 'utf8');

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

const QCV_SECTION_MARKER = 'QCV8001 Web 外觀對齊';

/**
 * 外觀覆寫刻意寫在檔案後段，前面的宣告仍被上面每一條幾何守衛檢查。
 * 若用 rule() 會 grab 到前段原值，刪掉整段外觀也不會紅，所以這裡從標記之後找。
 */
function refRule(selector) {
  const sectionAt = cssSource.indexOf(QCV_SECTION_MARKER);
  expect(sectionAt, `styles.css 找不到 ${QCV_SECTION_MARKER} 區段`).toBeGreaterThan(-1);
  const at = cssSource.indexOf(`${selector} {`, sectionAt);
  expect(at, `QCV8001 外觀區段找不到 ${selector}`).toBeGreaterThan(-1);
  return cssSource.slice(at, cssSource.indexOf('}', at));
}

describe('four feature tabs with in-page sub-tabs', () => {
  it('wraps the tab strip so labels do not overflow on narrow screens', () => {
    // 主頁籤在英文介面比中文長；單列會被擠爆並把後面的頁籤推出畫面。
    expect(rule('.tabs')).toMatch(/flex-wrap:\s*wrap/);
  });

  it('never wraps a tab label', () => {
    // 頁籤文字摺行會讓同一列的頁籤高度不一致，看起來像被切半。
    expect(rule('.tab')).toMatch(/white-space:\s*nowrap/);
  });

  it('hides inactive panels via the hidden attribute, not visibility', () => {
    // app.js 用 `panel.hidden = ...`；若 CSS 沒有對應規則，display 預設值會蓋掉
    // hidden，所有面板會同時顯示。
    expect(cssSource).toMatch(/\.panel\[hidden\] \{ display: none; \}/);
    expect(appSource).toMatch(/panel\.hidden = panel\.dataset\.panel !== name/);
  });

  it('wraps the sub-tab strip too, and keeps its labels on one line', () => {
    expect(rule('.subtabs')).toMatch(/flex-wrap:\s*wrap/);
    expect(rule('.subtab')).toMatch(/white-space:\s*nowrap/);
  });

  it('hides inactive sub-panels via the hidden attribute', () => {
    // 同一組次分頁若沒有這條規則，截圖／PTZ／監看牆會同時顯示在同一頁。
    expect(cssSource).toMatch(/\.subpanel\[hidden\] \{ display: none; \}/);
    expect(appSource).toMatch(/panel\.hidden = panel\.dataset\.subpanel !== name/);
  });

  it('restores every group\'s chosen sub-tab when the primary tab is shown again', () => {
    // 少了這段，切去別的主頁再回來時，次分頁的 active 標記與可見性會不一致：
    // 標記停在「截圖」而畫面回到監看牆（或反過來），操作員會以為按錯。
    expect(appSource).toMatch(/for \(const \[group, sub\] of activeSubTab\) \{\s*showSubTab\(group, sub\);/);
  });

  it('binds sub-tab buttons to the group their own toolbar declares', () => {
    // 群組寫在按鈕所屬的工具列上；若改成硬編碼，第二組次分頁的按鈕會去切第一組。
    expect(appSource).toMatch(/btn\.closest\('\[data-subtab-group\]'\)\?\.dataset\.subtabGroup/);
  });
});

describe('per-cell live tools', () => {
  it('overlays the cell toolbar instead of pushing the video down', () => {
    expect(rule('.live-cell-actions')).toMatch(/position:\s*absolute/);
  });

  it('keeps the cell buttons visible without hover', () => {
    // 大螢幕與觸控面板常常沒有 hover；只在 :hover 顯示等於點不到。
    const block = rule('.live-cell-actions');
    expect(block).not.toMatch(/opacity:\s*0;/);
    expect(block).toMatch(/opacity:/);
  });

  it('labels every cell button with a translated name, not just a glyph', () => {
    // 螢幕閱讀器唸不出「⛶」；aria-label 與 title 都要有，且必須走翻譯函式。
    for (const key of ['cell.fullscreen', 'cell.snapshot', 'cell.ptz']) {
      const use = appSource.match(new RegExp(`title="\\$\\{tx\\('${key}'\\)\\}"[^>]*aria-label="\\$\\{tx\\('${key}'\\)\\}"`));
      expect(use, key).not.toBeNull();
    }
  });

  it('acts on the cell that was clicked, not on a panel-wide channel field', () => {
    // 這條守的是「按了截圖卻抓到另一台」：格子的頻道要從 liveCells 的同一格拿。
    expect(appSource).toMatch(/const entry = liveCells\[liveCellIndex\(cell\)\]/);
    expect(appSource).toMatch(/captureCellFrame\(entry\?\.video, channelId\)/);
  });

  it('delegates the cell clicks from the grid container so redraws do not lose them', () => {
    // 格線每次開檔都整批重畫；逐格綁定會漏掉重畫後的按鈕。
    expect(appSource).toMatch(/\$\('live-grid'\)\.addEventListener\('click', onLiveGridClick\)/);
  });

  it('carries the cell channel into the snapshot and PTZ pages', () => {
    expect(appSource).toMatch(/function openCellTool\(group, name, channelId\)/);
    expect(appSource).toMatch(/openCellTool\('live', 'ptz', channelId\)/);
    expect(appSource).toMatch(/name === 'snapshot' \? \$\('snap-channel'\) : \$\('ptz-channel'\)/);
  });
});

describe('live layout picker', () => {
  it('keeps all layout buttons reachable and marks the active one', () => {
    // 版面按鈕是唯一的開播入口；換行也不能被裁掉，目前版面必須一眼看得出來。
    expect(rule('.layout-picker')).toMatch(/display:\s*flex/);
    expect(rule('.layout-picker')).toMatch(/flex-wrap:\s*wrap/);
    expect(rule('.layout-picker button.active')).toMatch(/background:\s*var\(--accent\)/);
    expect(indexSource).not.toContain('id="live-channels"');
  });
});

describe('horizontal overflow', () => {
  it('lets a panel scroll horizontally instead of clipping its table', () => {
    // 事件搜尋有 6 欄（含「跳至回放」動作），內容一長就撐破面板右緣；
    // 面板沒有捲動容器的話，多出來的部分直接被裁掉，右側的動作欄永遠看不到。
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

describe('device info and export panels', () => {
  it('arranges the info tables side by side on wide screens', () => {
    // Info 頁的兩張表（系統／儲存）在寬螢幕並排；疊放的話要捲很久才看得到磁碟表。
    expect(rule('.info-grid')).toMatch(/display:\s*grid/);
    expect(rule('.info-grid')).toMatch(/repeat\(auto-fit,\s*minmax\(min\(320px, 100%\), 1fr\)\)/);
  });

  it('keeps the key column narrow so long values wrap instead of pushing the table', () => {
    // 鍵值表的欄名（中文短、英文長）若不固定寬度，值欄會被擠出捲動容器。
    expect(rule('.kv th')).toMatch(/width:\s*1%/);
    expect(rule('.kv td')).toMatch(/white-space:\s*normal/);
  });

  it('ships distinct status badge styles for export jobs', () => {
    // 佇列／執行中／完成／失敗四態只能靠這個徽章分辨；樣式消失時操作員
    // 要逐列讀英文狀態字串。
    expect(cssSource).toMatch(/\.export-status \{/);
    expect(cssSource).toMatch(/\.export-status\.ok \{/);
    expect(cssSource).toMatch(/\.export-status\.run \{/);
    expect(cssSource).toMatch(/\.export-status\.fail \{/);
  });
});

describe('QCV8001 Web 外觀對齊', () => {
  it('外觀覆寫使用參考機的深灰殼層與青色選取', () => {
    // 這些色碼是參考站 main.css / liveWasm.css 的辨識特徵；少掉任何一項，
    // 畫面仍功能正常但看起來完全是另一套系統，所以舊的版面測試不會紅。
    const vars = refRule(':root');
    for (const [name, color] of Object.entries({
      '--shell-deep': '#35363a',
      '--shell': '#505b69',
      '--toolbar': '#c7ccd3',
      '--ref-accent': '#00bbdb',
    })) {
      expect(vars, name).toMatch(new RegExp(`${name}:\\s*${color}`));
    }
    expect(refRule('body')).toMatch(/background:\s*var\(--shell-deep\)/);
    expect(refRule('.topbar')).toMatch(/background:\s*var\(--shell\)/);
    expect(refRule('.tabs')).toMatch(/background:\s*var\(--shell\)/);
    expect(refRule('.tab.active')).toMatch(/background:\s*var\(--ref-accent\)/);
    expect(refRule('.subtabs')).toMatch(/background:\s*var\(--toolbar\)/);
  });

  it('登入頁自成全頁，主介面未通過憑證前保持隱藏', () => {
    // .login-screen / #app-shell 都設了 display，單靠 HTML 的 hidden 會被蓋掉；
    // 少了這條規則，未登入時主介面仍然整頁可見，只是所有 API 回 401。
    expect(cssSource).toMatch(/#app-shell\[hidden\],\s*\.login-screen\[hidden\] \{ display: none; \}/);
    expect(cssSource).toMatch(/\.login-screen \{[\s\S]*?min-height: 100vh/);
    // 登入頁是獨立檔案：一張全螢幕卡片，不再藏在 index.html 的 hidden 區塊裡。
    expect(loginHtml).toMatch(/<div id="login-screen" class="login-screen">/);
    expect(loginHtml).not.toMatch(/hidden/);
    expect(indexSource).toMatch(/<div id="app-shell" hidden>/);
    expect(indexSource).not.toMatch(/class="login-screen"/);
  });

  it('登入卡片與事件表頭沿用 Login 與事件查詢的醒目配色', () => {
    expect(refRule('.login-card')).toMatch(/background:\s*#465363/);
    expect(refRule('#login button')).toMatch(/background:\s*var\(--ref-accent\)/);
    expect(refRule('#events thead th')).toMatch(/background:\s*#f0f0a0/);
    expect(refRule('.search-section')).toMatch(/gap:\s*38px/);
  });

  it('事件表單提供日期範圍與來源類型欄位', () => {
    // fun/event/query.html 的欄位橫排是操作員辨識「事件搜尋」的依靠；
    // 只保留關鍵字單欄雖然能查，但時間與來源得先猜 API 格式。
    expect(indexSource).toMatch(/id="search-form" class="row-form search-section"/);
    for (const id of ['event-from', 'event-to', 'event-type']) {
      expect(indexSource, id).toMatch(new RegExp(`id="${id}"`));
    }
    for (const [value, key] of [
      ['all', 'common.all'],
      ['alarm', 'replay.sourceAlarm'],
      ['access', 'replay.source'],
      ['edge', 'replay.sourceEdgeAi'],
    ]) {
      expect(indexSource, value).toMatch(new RegExp(`<option value="${value}" data-i18n="${key.replace('.', '\\.')}"`));
    }
    expect(indexSource).toMatch(/<option value="pos">POS<\/option>/);
  });

  it('回放工具列提供上一張下一張三十秒跳躍速度循環與全螢幕', () => {
    // player.html 的 bk30s/nextFrame/fw30s/fullScreen 是操作員辨識「這是回放播放器」的依靠；
    // 桌面的逐幀、速度、循環也都要能在 Web 上用滑鼠操作，不該只靠快捷鍵或拖游標。
    expect(refRule('.play-toolbar')).toMatch(/background:\s*var\(--toolbar\)/);
    for (const [id, key] of [
      ['play-prev-frame', 'play.prevFrame'],
      ['play-next-frame', 'play.nextFrame'],
      ['play-back30', 'play.back30'],
      ['play-forward30', 'play.forward30'],
      ['play-fullscreen', 'cell.fullscreen'],
    ]) {
      expect(indexSource, id).toMatch(new RegExp(`id="${id}"[^>]*data-i18n="${key.replace('.', '\\.')}"`));
    }
    // 速度與循環是表單控制項，不是按鈕：要有 i18n 化的可讀名稱，且 1× 必須是預設。
    expect(indexSource, 'play-speed').toMatch(/<select id="play-speed"[^>]*data-i18n-attr="aria-label:play\.speed"[\s\S]*?<\/select>/);
    expect(indexSource, 'play-speed default').toMatch(/<option value="1" selected>1×<\/option>/);
    expect(indexSource, 'play-loop').toMatch(/<input id="play-loop"[^>]*type="checkbox"/);
    expect(refRule('.play-toolbar select')).toMatch(/min-width:/);
    expect(refRule('.play-toolbar-field')).toMatch(/align-items:\s*center/);
  });

  it('事件表有動作欄可以從事件跳到回放', () => {
    // 只有時間沒有動作時，操作員得抄事件時間再手動填回放表單；
    // 欄頭一定要有 i18n，英文介面不能出現孤立的一格。
    const head = indexSource.match(/<table id="events">[\s\S]*?<\/thead>/)?.[0] ?? '';
    expect(head).toMatch(/data-i18n="col\.time"/);
    expect(head).toMatch(/data-i18n="col\.status"/);
    expect(head).toMatch(/data-i18n="col\.action"/);
    expect((head.match(/<th\b/g) ?? []).length).toBe(6);
  });
});
