import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
const stylesSource = readFileSync(join(root, 'styles.css'), 'utf8');
// M243：介面文字搬到 i18n.js 後，原始碼斷言不能只看 app.js，
// 否則「字串還在不在」會被誤判成翻譯被刪掉。這裡同時確認 key 有被使用、且目錄裡真的有譯文。
const i18nSource = readFileSync(join(root, 'i18n.js'), 'utf8');

/** 斷言某個 key 被 app.js 引用，且目錄中確實有對應譯文。 */
function expectLocalized(source, key, zhTW) {
  expect(source).toContain(`t('${key}')`);
  expect(i18nSource).toContain(`'${key}': '${zhTW}'`);
}

describe('alarm board consolidation', () => {
  it('renders the board from the validated alarm-board API', () => {
    expect(appSource).toMatch(/\/api\/alarm-board\?take=200/);
    expect(appSource).toMatch(/\/api\/alarm-board\/summary/);
  });

  it('filters the board rows through the chip filter state', () => {
    expect(appSource).toMatch(/filterBoardRows\(rows, boardFilter\)/);
    expect(appSource).toMatch(/data-board-filter/);
  });

  it('has no legacy alarms/board path, renderer, or escalation glyph', () => {
    expect(appSource).not.toMatch(/\/api\/alarms\/board/);
    expect(appSource).not.toMatch(/refreshBoard/);
    expect(appSource).not.toMatch(/board-summary/);
    expect(appSource).not.toMatch(/data-p="critical"/);
  });

  it('keeps the dashboard single-source boot/poll entry point', () => {
    expect(appSource).toMatch(/setInterval\(\(\) => \{[\s\S]*?\}/);
    expect(appSource).toMatch(/async function refreshAll\(\)[\s\S]*renderBoard\(\)[\s\S]*renderNotifs\(\)[\s\S]*renderExports\(\)/);
  });
});

describe('dashboard live polling', () => {
  it('polls on an interval', () => {
    expect(appSource).toMatch(/POLL_MS[\s\S]*setInterval/);
  });

  it('pauses while the tab is hidden or an interactive control is focused', () => {
    expect(appSource).toMatch(/pollGate\(document\.activeElement\?\.tagName, document\.hidden\)/);
  });
});

describe('accounts area', () => {
  it('has no traces of old alert-panel toggle', () => {
    expect(indexSource).not.toMatch(/id="alarm-panel"/);
  });
});

describe('api-key flow', () => {
  it('refreshes the dashboard immediately when the API key is committed with Enter', () => {
    expect(appSource).toMatch(/apikey'\)\.addEventListener\('keydown'[\s\S]*Enter[\s\S]*refreshAll\(\)/);
  });
});

describe('search feedback', () => {
  it('reports empty result sets and truncation', () => {
    expectLocalized(appSource, 'state.noResults', '沒有符合的結果');
    expect(appSource).toMatch(/page\.total/);
  });

  it('guards empty queries with a hint', () => {
    expect(appSource).toMatch(/if \(!q\)[\s\S]*t\('state\.needKeyword'\)/);
    expect(i18nSource).toContain("'state.needKeyword': '請輸入關鍵字'");
  });

  it('adds a live region under the search table', () => {
    expect(indexSource).toMatch(/id="events-msg"[^>]*aria-live="polite"/);
  });
});

describe('search-to-action', () => {
  it('labels the search source column and action header', () => {
    expect(indexSource).toMatch(/<th scope="col"[^>]*data-i18n="col\.source"/);
    expect(indexSource).toMatch(/<th scope="col"[^>]*data-i18n="col\.operation"/);
  });

it('renders per-hit source names', () => {
    // 翻譯必須在呼叫時求值，否則切換語言後這張來源表會停在舊語言。
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES = \{ 1: \(\) => t\('replay\.sourceAlarm'\), 2: \(\) => t\('replay\.source'\), 4: \(\) => 'POS', 8: \(\) => t\('replay\.sourceEdgeAi'\) \}/);
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES\[source\]\?\.\(\)/);
  });

  it('shows alarm hit id and status, and gates 確認/誤報 actions to alarm hits for actors', () => {
    expect(appSource).toMatch(/data-sack="\$\{Number\(sid\)\}"/);
    expect(appSource).toMatch(/data-sfa="\$\{Number\(sid\)\}"/);
    expect(appSource).toMatch(/can && source === 1/);
    expect(appSource).toMatch(/esc\(row\.status \|\| '—'\)/);
  });

  it('refreshes the same search after acting on a hit', () => {
    expect(appSource).toMatch(/bindSearchActions\(\(\) => searchEvents\(q\)\)/);
  });

  it('re-syncs the board counts after a search-row action', () => {
    const actions = appSource.match(/refresh\(\);\s*renderBoard\(\);/g) ?? [];
    expect(actions.length).toBe(2);
  });

  it('reuses the alarm-board disposition endpoints through the authenticated transport', () => {
    expect(appSource).toMatch(/\/api\/alarm-board\/\$\{btn\.dataset\.sack\}\/ack/);
    expect(appSource).toMatch(/\/api\/alarm-board\/\$\{btn\.dataset\.sfa\}\/disposition/);
  });
});

describe('connection banner', () => {
  it('adds a live status element in the top bar', () => {
    expect(indexSource).toMatch(/id="conn"[^>]*role="status"[^>]*aria-live="polite"/);
  });

  it('flips the banner inside the single transport around fetch', () => {
    expect(appSource).toMatch(/const response = await fetch\(path, \{ \.\.\.init, headers \}\);\s*setConn\(true\);/);
    expect(appSource).toMatch(/catch \(err\) \{\s*setConn\(false\);\s*throw err;\s*\}/);
  });

it('renders a disconnect label and hides empty state', () => {
    expect(appSource).toMatch(/el\.textContent = ok \? '' : t\('state\.disconnected'\);/);
    expect(i18nSource).toContain("'state.disconnected': '連線中斷'");
  });
});

describe('license banner', () => {
  it('adds a live status element beside the connection badge', () => {
    expect(indexSource).toMatch(/id="lic"[^>]*role="status"[^>]*aria-live="polite"/);
  });

  it('polls license state alongside the rest of the dashboard', () => {
    // 只在 boot 取一次不夠：授權可能在執行中被匯入或到期。
    expect(appSource).toMatch(/await refreshLicense\(\);\s*await refreshAll\(\);/);
    expect(appSource).toMatch(/refreshLicense\(\);\s*refreshAll\(\);/);
  });

it('names the missing features instead of leaving an empty panel', () => {
    expect(appSource).toMatch(/apiRaw\('\/api\/license'\)/);
    expect(appSource).toMatch(/tx?\('state\.unauthorized'/);
    expect(i18nSource).toContain("'state.unauthorized': '未授權：{reason}'");
    expect(appSource).toMatch(/\.filter\(\(f\) => !f\.allowed\)/);
  });

  it('never inserts license text as HTML', () => {
    const fn = appSource.slice(
      appSource.indexOf('async function refreshLicense'),
      appSource.indexOf('async function refreshAll'),
    );
    expect(fn).not.toMatch(/innerHTML/);
  });

  it('reuses the server-side expiry wording instead of rewriting it', () => {
    const fn = appSource.slice(
      appSource.indexOf('async function refreshLicense'),
      appSource.indexOf('async function refreshAll'),
    );
    expect(fn).toMatch(/status\.expiryMessage/);
    expect(fn).not.toMatch(/剩 \$\{/);
  });
});

/**
 * 閘門是整條路徑一起擋的，所以同一面板的每個動作都可能拿到 403 與同一句 error。
 * 動作層不顯示出來的話，使用者只會看到畫面原封不動地回來，像是按了沒反應；
 * 而讀取層若把 403 吞成空清單，「沒授權」看起來就跟「這個頻道沒資料」一樣。
 * 這裡的清單刻意從伺服器的閘門表格推導出來：閘門多擋一條路徑，這條測試就會逼 SPA 補回饋。
 */
const gateSource = readFileSync(
  fileURLToPath(new URL('../HeliVMS.WebApi/LicenseGateMiddleware.cs', import.meta.url)),
  'utf8',
);
const serverGatedPrefixes = [...gateSource.matchAll(/\("(\/api\/[\w/]+)",\s*LicenseFeatures\.\w+\)/g)].map(
  (m) => m[1],
);
const spaApiLiterals = [...appSource.matchAll(/['`](\/api\/[A-Za-z0-9_-]*)/g)].map((m) => m[1]);
const appLines = appSource.split(/\r?\n/);

/** 取出 `function name(...)` 到下一個頂層 function 宣告之間的原始碼。 */
function functionBody(name) {
  const start = appSource.search(new RegExp(`^(?:async )?function ${name}\\(`, 'm'));
  expect(start, `${name} 找不到`).toBeGreaterThan(-1);
  const rest = appSource.slice(start + 1);
  const next = rest.search(/^(?:async )?function \w+\(/m);
  return appSource.slice(start, next === -1 ? undefined : start + 1 + next);
}

/** 面板讀取函式必須自己清掉筆數，而不是沿用上一次成功的畫面。 */
const gatedRenders = [
  ['renderSchedules', 'sched-count'],
  ['renderPatrols', 'patrol-count'],
  ['renderDetections', 'det-count'],
  ['renderProviders', 'provider-count'],
  ['renderShares', 'share-count'],
];

/** 列內動作（刪除／撤銷／切換）同樣要回報未授權。 */
const gatedRowActions = [
  ['renderSchedules', 'data-sched-del', 'sched-msg'],
  ['renderPatrols', 'data-patrol-del', 'patrol-msg'],
  ['renderProviders', 'data-provider-toggle', 'provider-msg'],
  ['renderProviders', 'data-provider-del', 'provider-msg'],
  ['renderShares', 'data-share-revoke', 'share-msg'],
];

/**
 * 取出某個 `[data-*]` 選擇器自己的那一段迴圈內容，到下一個 `[data-*]` 為止。
 * 邊界要真的收斂：直接切到檔尾的話，別處的 feedback 會讓斷言永遠成立。
 */
function rowActionBlock(fn, action) {
  const body = functionBody(fn);
  const at = body.indexOf(`[${action}]`);
  expect(at, `${fn} 內找不到 ${action} 的 handler`).toBeGreaterThan(-1);
  const rest = body.slice(at);
  const next = rest.indexOf('[data-', 1);
  return next === -1 ? rest : rest.slice(0, next);
}

describe('license denial feedback on gated panels', () => {
  it('parses the server gate table so new prefixes cannot slip through unnoticed', () => {
    expect(serverGatedPrefixes.length).toBeGreaterThanOrEqual(5);
  });

  it('reads every gated prefix the SPA touches through gatedFetch', () => {
    for (const prefix of serverGatedPrefixes) {
      if (!spaApiLiterals.some((url) => url.startsWith(prefix))) continue;
      const line = appLines.find((l) => l.includes('gatedFetch(') && l.includes(prefix));
      expect(line, `${prefix} 在 SPA 有用到，但沒有任何面板用 gatedFetch 回報未授權`).toBeTruthy();
      const msg = /gatedFetch\('([\w-]+)'/.exec(line)[1];
      expect(indexSource, `${msg} 必須是 live region`).toMatch(
        new RegExp(`id="${msg}"[^>]*aria-live="polite"`),
      );
    }
  });

  it('never swallows a gated read into an empty list', () => {
    for (const prefix of serverGatedPrefixes) {
      expect(appSource, `${prefix} 不可用 api() 直接讀，否則 403 會變成空清單`).not.toMatch(
        new RegExp(`api\\(['\`]${prefix}`),
      );
    }
  });

  it('clears the row count instead of rendering zero rows when denied', () => {
    for (const [fn, count] of gatedRenders) {
      const body = functionBody(fn);
      expect(body, `${fn} 必須處理 gatedFetch 的 null`).toMatch(/if \(!list\) \{/);
      expect(body, `${fn} 被擋下時要清掉 ${count}`).toMatch(
        new RegExp(`if \\(!list\\) \\{[\\s\\S]*?\\$\\('${count}'\\)\\.textContent = '';`),
      );
      expect(body, `${fn} 必須從 gatedFetch 取得資料`).toMatch(/gatedFetch\('/);
    }
  });

it('shows the server denial text instead of a generic failure', () => {
    const body = functionBody('feedback');
    expect(body).toMatch(/info\?\.error \?\? t\('state\.failed'\)/);
    expect(body).toMatch(/t\('state\.disconnected'\)/);
    expect(body).toMatch(/resp\.ok \? okText\(info\)/);
    expect(body).toMatch(/return resp\.ok;/);
    // fallback 訊息必須真的有譯文，否則閘門擋下時會顯示 ‼state.failed。
    expect(i18nSource).toContain("'state.failed': '失敗'");
    expect(i18nSource).toContain("'state.disconnected': '連線中斷'");
  });

  it('adds a live region to every gated panel', () => {
    for (const [, , msg] of gatedRowActions) {
      expect(indexSource).toMatch(new RegExp(`id="${msg}"[^>]*aria-live="polite"`));
    }
  });

  it('reports the denial for row-level actions too', () => {
    for (const [fn, action, msg] of gatedRowActions) {
      expect(rowActionBlock(fn, action), `${action} 被閘門擋下時必須回報到 ${msg}`).toMatch(
        new RegExp(`feedback\\('${msg}'`),
      );
    }
  });

  it('reverts the provider checkbox when the server refuses the toggle', () => {
    // 只顯示錯誤不還原勾選，畫面會停在一個伺服器根本沒接受的狀態。
    expect(rowActionBlock('renderProviders', 'data-provider-toggle')).toMatch(
      /if \(\!\(await feedback\('provider-msg', resp\)\)\) \{[\s\S]*?cb\.checked = !cb\.checked;/,
    );
  });

  it('no longer swallows the create response with ok?.ok', () => {
    expect(appSource).not.toMatch(/if \(ok\?\.ok\) renderSchedules\(\);/);
    expect(appSource).not.toMatch(/if \(ok\?\.ok\) renderPatrols\(\);/);
  });
});

describe('board freshness', () => {
  it('labels the overdue column it already renders', () => {
    expect(indexSource).toMatch(/<th scope="col"[^>]*data-i18n="col\.overdue"/);
  });

  it('adds a last-updated meta line plus manual refresh', () => {
    expect(indexSource).toMatch(/id="alarm-meta"/);
    expect(indexSource).toMatch(/id="board-refresh"[^>]*>立即更新/);
  });

it('stamps the render time and wires the refresh button', () => {
    // 時間戳必須跟著語系走（en 走 12 小時制），否則英文介面會出現 24 小時制的時間。
    expect(appSource).toMatch(/t\('board\.updatedAt', \{ time: new Date\(\)\.toLocaleTimeString\(locale\(\)\) \}\)/);
    expect(appSource).toMatch(/\$\(['"]board-refresh['"]\)\?\.addEventListener\(['"]click['"], \(\) => renderBoard\(\)\)/);
  });
});

describe('admin quick nav', () => {
  it('lists admin section jump links below the topbar', () => {
    expect(indexSource).toMatch(/id="admin-nav"[^>]*hidden[^>]*aria-label="管理區段"/);
    expect(indexSource).toMatch(/<a href="#accounts-panel"[^>]*data-i18n="col\.account"/);
    expect(indexSource).toMatch(/<a href="#health-panel"[^>]*data-i18n="nav\.health"/);
  });

  it('shows the nav only for actors in chrome', () => {
    expect(appSource).toMatch(/\$\(['"]admin-nav['"]\)\.hidden = !admin;/);
  });

  it('reserves scroll space for anchored sections', () => {
    expect(stylesSource).toMatch(/section\.panel\[id\] \{ scroll-margin-top: 12px; \}/);
  });
});
describe('remote playback (M239 HLS)', () => {
it('exposes a playback panel with an accessible form and a video element', () => {
    // M243：標題文字帶 data-i18n，斷言改看結構與 data-i18n，避免綁死某一種語言。
    // data-i18n 必須在只包文字的 span 上，不能在含 <video>／計數 span 的 <h2> 上，
    // 否則 applyDom 的 textContent 會刪掉那些子節點。
    expect(indexSource).toMatch(/<h2[^>]*>\s*<span data-i18n="nav\.remotePlayback">/);
    expect(indexSource).toMatch(/<video id="play-video"[^>]*controls[^>]*playsinline/);
    expect(indexSource).toMatch(/id="play-channel"[^>]*aria-label="頻道編號"/);
    expect(indexSource).toMatch(/id="play-from"[^>]*aria-label="起始時間"/);
    expect(indexSource).toMatch(/id="play-to"[^>]*aria-label="結束時間"/);
    expect(indexSource).toMatch(/id="play-msg"[^>]*aria-live="polite"/);
  });

  it('fetches the playlist through the single authenticated transport', () => {
    expect(appSource).toMatch(/apiRaw\(buildPlaylistQuery\(\{ channelId, stream: 'main', from, to \}\)\)/);
    expect(appSource).toMatch(/const playlist = parseM3u8\(await response\.text\(\)\)/);
  });

  it('pushes the init segment and every media segment through the authenticated transport', () => {
    expect(appSource).toMatch(/if \(playlist\.initUri\) await push\(playlist\.initUri\)/);
    expect(appSource).toMatch(/for \(const segment of playlist\.segments\) await push\(segment\.uri\)/);
    expect(appSource).toMatch(/const response = await apiRaw\(uri\)/);
    expect(appSource).toMatch(/media\.endOfStream\(\)/);
  });

  it('never plays a segment with a bare fetch that would skip the API key', () => {
    const playerBody = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(playerBody).not.toMatch(/[^.\w]fetch\(/);
  });

  it('reports unsupported browsers instead of failing silently', () => {
    expect(appSource).toMatch(/'MediaSource' in window/);
    expect(appSource).toMatch(/msg\.textContent = err\.message/);
  });

  it('wires the form and seeds a one-hour default window', () => {
    expect(appSource).toMatch(/\$\(['"]play-form['"]\)\.addEventListener\(['"]submit['"], playRemote\)/);
    expect(appSource).toMatch(/defaultPlayWindow\(\)/);
  });
});
