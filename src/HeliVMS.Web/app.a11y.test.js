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

  it('does not gate a nonexistent alarm panel (the board is dimmed via .gated instead)', () => {
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
    const labeled = ['user', 'pass', 'q', 'new-user', 'new-pass', 'audit-actor', 'door-card', 'det-conf', 'det-class', 'timeline-day'];
    for (const id of labeled) {
      const tag = indexSource.match(new RegExp(`<[^>]+id="${id}"[^>]*>`))?.[0] ?? '';
      expect(tag, `#${id}`).toMatch(/aria-label=/);
    }
  });

  it('focuses the username field on load', () => {
    expect(indexSource).toMatch(/<input id="user"[\s\S]*?autofocus/);
  });

  it('labels every table header with a column scope', () => {
    expect((indexSource.match(/<th>/g) ?? []).length).toBe(0);
    expect(indexSource).toMatch(/<th scope="col">/);
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

  it('gives the alarm-board action buttons readable labels (no cryptic glyphs)', () => {
    // M243：標籤文字改由目錄提供，這裡要驗的是「按鈕有可讀標籤」這件事，
    // 而不是標籤剛好是哪一種語言的字。
    expect(appSource).toMatch(/\$\{tx\('action\.triage'\)\}/);
    expect(appSource).toMatch(/\$\{tx\('action\.confirm'\)\}/);
    expect(appSource).toMatch(/\$\{tx\('common\.falseAlarm'\)\}/);
    expect(appSource).not.toMatch(/>!!</);
  });
});

describe('live regions and panels', () => {
  it('keeps the smartwall section summary in sync with the topbar badge', () => {
    expect(elementIds(indexSource)).toContain('sw-panel');
    expect(appSource).toMatch(/\$\('sw-panel'\)/);
  });
});