import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');

describe('alarm board consolidation', () => {
  it('renders the board from the validated alarm-board API', () => {
    expect(appSource).toMatch(/\/api\/alarm-board\?take=200/);
    expect(appSource).toMatch(/\/api\/alarm-board\/summary/);
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