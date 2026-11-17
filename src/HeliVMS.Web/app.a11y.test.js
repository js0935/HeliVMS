import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const appSource = readFileSync(fileURLToPath(new URL('./app.js', import.meta.url)), 'utf8');
const indexSource = readFileSync(fileURLToPath(new URL('./index.html', import.meta.url)), 'utf8');
const cssSource = readFileSync(fileURLToPath(new URL('./styles.css', import.meta.url)), 'utf8');

const elementIds = (source) => [...source.matchAll(/id="([^"]+)"/g)].map((m) => m[1]);
const appIdRefs = () => [...new Set([...appSource.matchAll(/\$\('([^']+)'\)/g)].map((m) => m[1]))];

describe('document structure', () => {
  it('has no duplicate element ids', () => {
    const ids = elementIds(indexSource);
    expect([...new Set(ids.filter((v, i) => ids.indexOf(v) !== i))]).toEqual([]);
  });

  it('every $() reference in app.js resolves to an element in index.html', () => {
    const ids = elementIds(indexSource);
    expect(appIdRefs().filter((r) => !ids.includes(r))).toEqual([]);
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
    const labeled = ['user', 'pass', 'q', 'live-channels', 'snap-channel', 'ptz-channel', 'ptz-preset-name', 'io-channel'];
    for (const id of labeled) {
      const tag = indexSource.match(new RegExp(`<[^>]+id="${id}"[^>]*>`))?.[0] ?? '';
      expect(tag, `#${id}`).toMatch(/aria-label=/);
    }
  });

  it('登入表單住在獨立畫面裡，主介面未通過憑證前不出現', () => {
    // 舊版把金鑰與登入表單塞進頂列 <details>，未登入時主介面仍整頁亮著。
    // 現在兩個區塊各自獨立，且由 showLogin() 控制 hidden。
    expect(indexSource).toMatch(/<div id="login-screen"[^>]*hidden>[\s\S]*?<form id="login"[^>]*>[\s\S]*?<\/div>/);
    expect(indexSource).toMatch(/<div id="app-shell" hidden>[\s\S]*?<header id="app" class="topbar">/);
    expect(indexSource).not.toMatch(/<details class="topbar-auth">/);
    expect(indexSource).not.toMatch(/<summary[^>]*autofocus/);
    expect(appSource).toMatch(/function showLogin\(show\)[\s\S]*\$\('login-screen'\)\.hidden = !show[\s\S]*\$\('app-shell'\)\.hidden = show/);
    // 靜態 autofocus 對初始 hidden 的元素無效，改由 showLogin() 在 unhide 後移動焦點。
    expect(appSource).toMatch(/requestAnimationFrame\(\(\) => \$\('user'\)\?\.focus\(\)\)/);
  });

  it('登入頁與主介面各有一個語系選擇器且 id 不重複', () => {
    expect(indexSource.match(/id="login-locale"/g)).toHaveLength(1);
    expect(indexSource.match(/id="locale"/g)).toHaveLength(1);
    expect(appSource).toMatch(/for \(const id of \['locale', 'login-locale'\]\)/);
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
