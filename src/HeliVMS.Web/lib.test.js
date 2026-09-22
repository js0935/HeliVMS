import { describe, expect, it } from 'vitest';
import {
  ackPayload,
  ackRate,
  accountRows,
  buildSegmentsQuery,
  buildTimelineQuery,
  canAct,
  configCard,
  eventRow,
  formatTimestamp,
  gapLabel,
  gridLayout,
  parseLogin,
  parseWsMessage,
  pinStyles,
  posTotals,
  priorityRank,
  smartwallSnapshot,
  sortBoard,
  summarizeBoard,
  tileClass,
  tileLabel,
  triagePayload,
} from './lib.js';

describe('formatTimestamp', () => {
  it('renders HH:MM:SS from ISO', () => {
    expect(formatTimestamp('2026-01-02T03:04:05Z')).toBe('03:04:05');
  });

  it('returns empty for invalid input', () => {
    expect(formatTimestamp('not-a-date')).toBe('');
    expect(formatTimestamp(undefined)).toBe('');
  });
});

describe('eventRow', () => {
  it('maps camelCase API payload', () => {
    expect(
      eventRow({ id: 7, startUtc: '2026-01-02T03:04:05Z', channelId: 1, eventType: 'motion', status: 'open' }),
    ).toEqual({ id: 7, time: '03:04:05', channel: 1, type: 'motion', status: 'open' });
  });
});

describe('board helpers', () => {
  it('ranks critical above high', () => {
    expect(priorityRank('critical')).toBeGreaterThan(priorityRank('high'));
    expect(priorityRank('unknown')).toBe(0);
  });

  it('sorts by priority desc then id', () => {
    const sorted = sortBoard([
      { eventId: 2, priority: 'high' },
      { eventId: 9, priority: 'critical' },
      { eventId: 1, priority: 'critical' },
    ]);
    expect(sorted.map((r) => r.eventId)).toEqual([9, 1, 2]);
  });

  it('summarizes total/critical/overdue', () => {
    const now = Date.parse('2026-01-01T12:00:00Z');
    const summary = summarizeBoard(
      [
        { priority: 'critical', dueUtc: '2026-01-01T11:00:00Z' },
        { priority: 'normal', dueUtc: '2026-01-01T13:00:00Z' },
        { priority: 'high' },
      ],
      now,
    );
    expect(summary).toEqual({ total: 3, critical: 1, overdue: 1 });
  });
});

describe('query builders', () => {
  it('builds timeline query with encoded ISO day', () => {
    const q = buildTimelineQuery({ channelId: 1, stream: 'main', day: '2026-01-02T00:00:00Z' });
    expect(q).toContain('/api/recording/timeline?');
    expect(q).toContain('channelId=1');
    expect(q).toContain('stream=main');
    expect(q).toContain('2026-01-02T00%3A00%3A00.000Z');
  });

  it('builds segments query window', () => {
    const q = buildSegmentsQuery({
      channelId: 3,
      stream: 'main',
      from: '2026-01-02T00:00:00Z',
      to: '2026-01-02T01:00:00Z',
    });
    expect(q).toContain('/api/recording/segments?');
    expect(q).toContain('channelId=3');
    expect(q).toContain('from=2026-01-02T00');
    expect(q).toContain('to=2026-01-02T01');
  });
});

describe('parseWsMessage', () => {
  it('accepts camelCase alert frames', () => {
    expect(parseWsMessage('{"kind":"alarm.triage","eventId":5,"priority":"high"}')).toEqual({
      kind: 'alarm.triage',
      eventId: 5,
      status: null,
      priority: 'high',
    });
  });

  it('accepts PascalCase frames too', () => {
    expect(parseWsMessage('{"Kind":"alarm.ack","EventId":8,"Status":"acknowledged"}')).toEqual({
      kind: 'alarm.ack',
      eventId: 8,
      status: 'acknowledged',
      priority: null,
    });
  });

  it('rejects garbage and wrong shapes', () => {
    expect(parseWsMessage('not json')).toBeNull();
    expect(parseWsMessage('{"kind":"x"}')).toBeNull();
    expect(parseWsMessage('null')).toBeNull();
  });
});

describe('gapLabel', () => {
  it('labels empty/full/partial coverage', () => {
    expect(gapLabel(1)).toBe('無錄影');
    expect(gapLabel(0)).toBe('全日');
    expect(gapLabel(0.42)).toContain('%');
  });
});

describe('gridLayout', () => {
  it('tiles n channels into cols x rows fractions', () => {
    const pins = gridLayout(8, 4);
    expect(pins).toHaveLength(8);
    expect(pins[0]).toEqual({ left: 0, top: 0, width: 0.25, height: 0.5 });
    expect(pins[7]).toEqual({ left: 0.75, top: 0.5, width: 0.25, height: 0.5 });
  });

  it('tolerates zero channels and bad cols', () => {
    expect(gridLayout(0)).toHaveLength(0);
    expect(gridLayout(2, 0)).toHaveLength(2);
  });
});

describe('pinStyles', () => {
  it('renders fractional pins as exact percentages with inset', () => {
    expect(pinStyles({ left: 0, top: 0, width: 1, height: 0.5 })).toEqual({
      left: '3.00%',
      top: '3.00%',
      width: '94.00%',
      height: '44.00%',
    });
  });

  it('clamps to non-negative sizes for oversized insets', () => {
    expect(pinStyles({ left: 0, top: 0, width: 0.01, height: 0.01 }).width).toBe('0.00%');
  });
});

describe('smartwallSnapshot', () => {
  const now = Date.parse('2026-01-01T12:00:00Z');

  it('keeps only fresh cells and buckets critical/highlight', () => {
    const snap = smartwallSnapshot(
      [
        { startUtc: '2026-01-01T11:59:00Z', priority: 'critical', highlight: false },
        { startUtc: '2026-01-01T11:58:30Z', priority: 'normal', highlight: true },
        { startUtc: '2026-01-01T10:50:00Z', priority: 'high', highlight: false },
      ],
      now,
    );
    expect(snap.count).toBe(2);
    expect(snap.cells[0].highlight).toBe(true);
    expect(snap.critical).toBe(1);
  });

  it('caps at 64 tiles', () => {
    const cells = Array.from({ length: 80 }, (_, i) => ({
      startUtc: '2026-01-01T11:59:00Z',
      priority: 'normal',
      highlight: false,
      id: i,
    }));
    expect(smartwallSnapshot(cells, now).cells).toHaveLength(64);
  });
});

describe('ackRate', () => {
  it('returns percentages and handles empty', () => {
    expect(ackRate([])).toBe(0);
    expect(ackRate([{ status: 'acknowledged' }, { status: 'open' }])).toBe(50);
  });
});

describe('posTotals', () => {
  it('sums amounts and groups by register', () => {
    const t = posTotals([
      { registerId: 'R1', amountCents: 100 },
      { registerId: 'R1', amountCents: 250 },
      { registerId: 'R2', amountCents: 50 },
    ]);
    expect(t.count).toBe(3);
    expect(t.totalCents).toBe(400);
    expect(t.registers.R1).toEqual({ count: 2, totalCents: 350 });
    expect(t.registers.R2).toEqual({ count: 1, totalCents: 50 });
  });
});

describe('action payload builders', () => {
  it('builds ack and triage bodies', () => {
    expect(ackPayload(7)).toEqual({ id: 7, body: { acknowledged: true } });
    expect(ackPayload(7, false)).toEqual({ id: 7, body: { acknowledged: false } });
    expect(triagePayload(9, 'critical')).toEqual({ id: 9, body: { priority: 'critical', dueUtc: null, owner: null } });
  });
});

describe('login gating', () => {
  it('gates acting on admin role', () => {
    expect(canAct('admin')).toBe(true);
    expect(canAct('viewer')).toBe(false);
    expect(canAct(null)).toBe(false);
  });

  it('parses login responses', () => {
    expect(parseLogin({ role: 'admin', displayName: '操作員' })).toEqual({
      ok: true,
      role: 'admin',
      displayName: '操作員',
    });
    expect(parseLogin({ error: '密碼錯誤' })).toEqual({ ok: false, error: '密碼錯誤' });
    expect(parseLogin(null)).toEqual({ ok: false, error: '回應異常' });
  });

  it('normalizes account rows and sorts by username', () => {
    const rows = accountRows([
      { Id: 2, Username: 'bob', Role: 'viewer', Enabled: 1, Locked: 0 },
      { id: 1, username: 'amy', role: 'admin', displayName: null, enabled: true, locked: false },
    ]);
    expect(rows.map((r) => r.username)).toEqual(['amy', 'bob']);
    expect(rows[1].enabled).toBe(true);
    expect(rows[1].locked).toBe(false);
    expect(accountRows(null)).toEqual([]);
  });

  it('normalizes config cards', () => {
    expect(configCard({ authEnabled: true, lockoutThreshold: 3, lockoutMinutes: 30 })).toEqual({
      authEnabled: true,
      lockoutThreshold: 3,
      lockoutMinutes: 30,
    });
    expect(configCard({ AuthEnabled: 1, LockoutThreshold: '4', LockoutMinutes: 15 })).toEqual({
      authEnabled: true,
      lockoutThreshold: 4,
      lockoutMinutes: 15,
    });
    expect(configCard(null)).toEqual({ authEnabled: false, lockoutThreshold: 5, lockoutMinutes: 5 });
  });

  it('maps smartwall tiles to classes and labels', () => {
    expect(tileClass({ priority: 'critical' })).toBe('tile-crit');
    expect(tileClass({ priority: 'normal' })).toBe('tile-norm');
    expect(tileClass({ priority: 'critical', highlight: true })).toBe('tile-hot');
    expect(tileClass({ Highlight: 1 })).toBe('tile-hot');
    expect(tileLabel({ channelId: 3, eventType: 'motion' })).toEqual({
      channel: 3,
      type: 'motion',
      priority: 'normal',
    });
  });
});