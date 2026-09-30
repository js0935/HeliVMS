import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
const stylesSource = readFileSync(join(root, 'styles.css'), 'utf8');

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
    expect(appSource).toMatch(/沒有符合的結果/);
    expect(appSource).toMatch(/page\.total/);
  });

  it('guards empty queries with a hint', () => {
    expect(appSource).toMatch(/if \(!q\)[\s\S]*請輸入關鍵字/);
  });

  it('adds a live region under the search table', () => {
    expect(indexSource).toMatch(/id="events-msg"[^>]*aria-live="polite"/);
  });
});

describe('search-to-action', () => {
  it('labels the search source column and action header', () => {
    expect(indexSource).toMatch(/<th scope="col">來源<\/th>/);
    expect(indexSource).toMatch(/<th scope="col">操作<\/th>/);
  });

  it('renders per-hit source names', () => {
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES = \{ 1: '警報', 2: '門禁', 4: 'POS', 8: '邊緣AI' \}/);
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES\[source\]/);
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
    expect(appSource).toMatch(/連線中斷/);
    expect(appSource).toMatch(/el\.textContent = ok \? '' : '連線中斷';/);
  });
});

describe('board freshness', () => {
  it('labels the overdue column it already renders', () => {
    expect(indexSource).toMatch(/<th scope="col">逾期<\/th>/);
  });

  it('adds a last-updated meta line plus manual refresh', () => {
    expect(indexSource).toMatch(/id="alarm-meta"/);
    expect(indexSource).toMatch(/id="board-refresh"[^>]*>立即更新/);
  });

  it('stamps the render time and wires the refresh button', () => {
    expect(appSource).toMatch(/更新於 \$\{new Date\(\)\.toLocaleTimeString\(\)\}/);
    expect(appSource).toMatch(/\$\(['"]board-refresh['"]\)\?\.addEventListener\(['"]click['"], \(\) => renderBoard\(\)\)/);
  });
});

describe('admin quick nav', () => {
  it('lists admin section jump links below the topbar', () => {
    expect(indexSource).toMatch(/id="admin-nav"[^>]*hidden[^>]*aria-label="管理區段"/);
    expect(indexSource).toMatch(/<a href="#accounts-panel">帳號<\/a>/);
    expect(indexSource).toMatch(/<a href="#health-panel">健康<\/a>/);
  });

  it('shows the nav only for actors in chrome', () => {
    expect(appSource).toMatch(/\$\(['"]admin-nav['"]\)\.hidden = !admin;/);
  });

  it('reserves scroll space for anchored sections', () => {
    expect(stylesSource).toMatch(/section\.panel\[id\] \{ scroll-margin-top: 12px; \}/);
  });
});