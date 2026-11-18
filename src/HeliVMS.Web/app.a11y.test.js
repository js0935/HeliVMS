import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const loginSource = readFileSync(fileURLToPath(new URL('./login.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');
const loginHtml = readFileSync(fileURLToPath(new URL('./login.html', import.meta.url)), 'utf8');
const cssSource = readFileSync(fileURLToPath(new URL('./styles.css', import.meta.url)), 'utf8');

const elementIds = (source) => [...source.matchAll(/id="([^"]+)"/g)].map((m) => m[1]);
const appIdRefs = () => [...new Set([...appSource.matchAll(/\$\('([^']+)'\)/g)].map((m) => m[1]))];
const loginIdRefs = () => [...new Set([...loginSource.matchAll(/\$\('([^']+)'\)/g)].map((m) => m[1]))];
/** 欄位現在分在兩頁，標籤檢查要在兩份 HTML 裡找。 */
const tagFor = (id) =>
  indexSource.match(new RegExp(`<[^>]+id="${id}"[^>]*>`))?.[0] ??
  loginHtml.match(new RegExp(`<[^>]+id="${id}"[^>]*>`))?.[0] ?? '';

describe('document structure', () => {
  it('has no duplicate element ids', () => {
    const ids = elementIds(indexSource);
    expect([...new Set(ids.filter((v, i) => ids.indexOf(v) !== i))]).toEqual([]);
  });

  it('every $() reference resolves to an element on its own page', () => {
    const mainIds = elementIds(indexSource);
    expect(appIdRefs().filter((r) => !mainIds.includes(r))).toEqual([]);
    const loginIds = elementIds(loginHtml);
    expect(loginIdRefs().filter((r) => !loginIds.includes(r))).toEqual([]);
  });

  it('has no duplicate element ids on the login page either', () => {
    const ids = elementIds(loginHtml);
    expect([...new Set(ids.filter((v, i) => ids.indexOf(v) !== i))]).toEqual([]);
  });

  it('does not reference the removed alarm board', () => {
    expect(appSource).not.toContain('alarm-panel');
  });
});

describe('keyboard accessibility', () => {
  it('renders a visible-on-focus skip link targeting the main content', () => {
    expect(indexSource).toMatch(/<a class="skip" href="#content"[^>]*data-i18n="a11y\.skipToContent"/);
    expect(indexSource).toMatch(/<main class="grid" id="content">/);
    expect(cssSource).toMatch(/\.skip:focus-visible/);
  });

  it('declares a focus-visible outline for every interactive control', () => {
    expect(cssSource).toMatch(/button:focus-visible/);
    expect(cssSource).toMatch(/input:focus-visible/);
    expect(cssSource).toMatch(/:focus-visible \{\s*outline: 2px solid var\(--accent\)/);
  });

  it('labels every high-value placeholder-only input with an aria-label', () => {
    const labeled = ['user', 'pass', 'q', 'snap-channel', 'ptz-channel', 'ptz-preset-name', 'io-channel'];
    for (const id of labeled) {
      expect(tagFor(id), `#${id}`).toMatch(/aria-label=/);
    }
  });

  it('回放工具列的速度與循環都有可讀名稱', () => {
    // select 只有數字選項時螢幕閱讀器唸不出「0.5×」是什麼；循環核取方塊也要有標籤，
    // 否則操作員只知道有一格打勾，不知道它會讓整段錄影重播。
    expect(tagFor('play-speed'), 'play-speed').toMatch(/data-i18n-attr="aria-label:play\.speed"/);
    expect(indexSource, 'play-speed label').toMatch(/<label[^>]*for="play-speed"[^>]*>[\s\S]*?<span data-i18n="play\.speed">/);
    expect(tagFor('play-loop'), 'play-loop').toMatch(/data-i18n-attr="aria-label:play\.loop"/);
    expect(indexSource, 'play-loop label').toMatch(/<label[^>]*>[\s\S]*?id="play-loop"[^>]*>[\s\S]*?<span data-i18n="play\.loop">[\s\S]*?<\/label>/);
  });

  it('exposes the live layout choice as labeled, pressed buttons', () => {
    // 版面現在是分頁器而不是下拉選單；螢幕閱讀器要唸得出群組名稱與目前按下的是哪一格。
    expect(indexSource).toMatch(/<div id="live-layout"[^>]*role="group"[^>]*data-i18n-attr="aria-label:live\.layout/);
    const buttons = indexSource.match(/<button type="button" data-layout="\d+"[^>]*>/g) ?? [];
    expect(buttons).toHaveLength(5);
    for (const button of buttons) expect(button).toMatch(/aria-pressed="(true|false)"/);
    expect(appSource).toMatch(/btn\.setAttribute\('aria-pressed', String\(active\)\)/);
  });

  it('登入表單住在獨立頁面，主介面未通過憑證前不出現', () => {
    // 舊版把金鑰與登入表單塞進頂列 <details>，未登入時主介面仍整頁亮著。
    // 現在是兩個 HTML：登入頁一載入就看得見，主介面則保持 hidden 直到 hasAccess() 通過。
    expect(loginHtml).toMatch(/<div id="login-screen" class="login-screen">[\s\S]*?<form id="login" class="login-fields">[\s\S]*?<\/div>/);
    expect(indexSource).toMatch(/<div id="app-shell" hidden>[\s\S]*?<header id="app" class="topbar">/);
    expect(indexSource).not.toMatch(/<form id="login"/);
    expect(indexSource).not.toMatch(/<div id="login-screen"/);
    expect(indexSource).not.toMatch(/<details class="topbar-auth">/);
    expect(indexSource).not.toMatch(/<summary[^>]*autofocus/);
    expect(appSource).toMatch(/\$\('app-shell'\)\.hidden = false;/);
    expect(appSource).toMatch(/if \(\$\('app-shell'\)\.hidden\) return;/);
    expect(appSource).toMatch(/if \(!\(await hasAccess\(\)\)\)/);
    // 登入頁一開始就可見，autofocus 直接用 rAF 移動焦點。
    expect(loginSource).toMatch(/requestAnimationFrame\(\(\) => \$\('user'\)\?\.focus\(\)\)/);
  });

  it('登入頁與主介面各有一個語系選擇器且 id 不重複', () => {
    expect(loginHtml.match(/id="login-locale"/g)).toHaveLength(1);
    expect(indexSource.match(/id="locale"/g)).toHaveLength(1);
    expect(indexSource).not.toMatch(/id="login-locale"/);
    expect(loginHtml).not.toMatch(/id="locale"/);
    expect(appSource).toMatch(/for \(const id of \['locale'\]\)/);
    expect(loginSource).toMatch(/\$\('login-locale'\)\.addEventListener\('change'/);
  });

  it('labels every table header with a column scope', () => {
    expect((indexSource.match(/<th>/g) ?? []).length).toBe(0);
    expect(indexSource).toMatch(/<th scope="col">/);
  });

  it('gives every PTZ direction button a readable label (no cryptic glyphs)', () => {
    // 方向盤上只有箭頭符號（↑↓←→↖↗↙↘），螢幕閱讀器念不出來；每一顆都必須有 aria-label。
    const buttons = indexSource.match(/<button[^>]*data-move="[^"]+"[^>]*>/g) ?? [];
    expect(buttons.length).toBe(10);
    for (const tag of buttons) {
      expect(tag).toMatch(/aria-label=/);
    }
  });
});

describe('screen readers', () => {
  it('marks every operation message region as aria-live', () => {
    const msgIds = elementIds(indexSource).filter((id) => /-msg$/.test(id));
    expect(msgIds).not.toEqual([]);
    for (const id of msgIds) {
      expect(indexSource).toMatch(new RegExp(`id="${id}"[^>]*aria-live="polite"`));
    }
  });

  it('announces the live alarm feed as a log region', () => {
    expect(indexSource).toMatch(/id="live-log"[^>]*role="log"[^>]*aria-live="polite"/);
  });
});

describe('tabs and panels are wired together', () => {
  it('points every primary tab at a panel that exists', () => {
    const ids = elementIds(indexSource);
    const tabs = [...indexSource.matchAll(/data-tab="([^"]+)"[^>]*aria-controls="([^"]+)"/g)];
    // 四個主頁：即時監看、回放、事件、設定。少一個就代表某個功能點不開。
    expect(tabs.map((m) => m[1]).sort()).toEqual(['events', 'live', 'playback', 'settings']);
    for (const [, , panel] of tabs) {
      expect(ids, panel).toContain(panel);
    }
  });

  it('marks every panel as a labelled tabpanel', () => {
    const ids = elementIds(indexSource);
    const panels = [...indexSource.matchAll(/data-panel="(?<name>[^"]+)"[^>]*role="tabpanel"[^>]*aria-labelledby="(?<label>[^"]+)"/g)];
    for (const { groups } of panels) {
      expect(ids, groups.label).toContain(groups.label);
    }
    const count = indexSource.match(/data-panel="/g) ?? [];
    expect(count.length).toBe(4);
  });

  it('points every sub-tab at a sub-panel in the same group', () => {
    // 次分頁是「功能雜亂」的解法：截圖／PTZ 收進監看、匯出收進回放、裝置資訊與 IO 收進設定。
    // 每一個都必須指到同組存在的面板，否則那一項按了完全沒反應（面板永遠 hidden）。
    const ids = elementIds(indexSource);
    const subs = [...indexSource.matchAll(/data-subtab="([^"]+)"[^>]*aria-controls="([^"]+)"/g)];
    expect(subs.map((m) => m[1]).sort()).toEqual([
      'export', 'general', 'info', 'io', 'ptz', 'snapshot', 'wall', 'player',
    ].sort());
    for (const [, , panel] of subs) {
      expect(ids, panel).toContain(panel);
    }
    // 每一組至少要有一個子面板與它配對。
    const groups = [...indexSource.matchAll(/data-subtab-group="([^"]+)"/g)].map((m) => m[1]);
    expect(groups.sort()).toEqual(['live', 'playback', 'settings']);
    for (const group of groups) {
      expect(indexSource, group).toContain(`data-subpanel-group="${group}"`);
    }
  });

  it('marks the tab strip as a tablist', () => {
    expect(indexSource).toMatch(/role="tablist"/);
    expect(appSource).toMatch(/btn\.setAttribute\('aria-selected', String\(on\)\)/);
  });
});
