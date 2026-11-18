import { describe, expect, it } from 'vitest';
import {
  ackPayload,
  ackRate,
  accountRows,
  auditFilter,
  auditRows,
  bearerHeader,
  buildSegmentsQuery,
  buildPlaylistQuery,
  buildCentralPlaylistQuery,
  buildRecordingClock,
  mseMimeType,
  parseFmp4InitTracks,
  parseFmp4VideoSpan,
  parseM3u8,
  playbackBufferedFor,
  playbackNeedsSegment,
  playbackRangesAheadToEvict,
  playbackRangesToEvict,
  playbackSeamStart,
  playlistMediaStarts,
  playlistSegmentIndexAt,
  PLAYLIST_WINDOW_LIMIT_S,
  buildTimelineQuery,
  canAct,
  configCard,
  dailyCard,
  esc,
  escPct,
  eventRow,
  filterBoardRows,
  focusLayout,
  formatLocalClock,
  formatTimestamp,
  liveChannelIds,
  evRows,
  backupRows,
  centralRows,
  centralChannelRows,
  centralAlarmRows,
  doorRows,
  detRows,
  notifRows,
  exportRows,
  providerRows,
  holdRows,
  ruleRows,
  shareRows,
  redRows,
  repRows,
  alarmRows,
  sysMetricsRows,
  sysTrendRows,
  sysDiskTrendRows,
  clipViewRows,
  fuseViewRows,
  smartwallLayout,
  audioRows,
  patrolLabel,
  scheduleInEffect,
  scheduleLabel,
  gapLabel,
  gridLayout,
  parseLogin,
  parseWsMessage,
  pinStyles,
  posTotals,
  pollGate,
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

describe('esc', () => {
  it('escapes every markup-significant character', () => {
    expect(esc('&<>"\'')).toBe('&amp;&lt;&gt;&quot;&#39;');
  });

  it('renders nullish as empty text', () => {
    expect(esc(null)).toBe('');
    expect(esc(undefined)).toBe('');
  });

  it('passes numbers and booleans through as text', () => {
    expect(esc(0)).toBe('0');
    expect(esc(false)).toBe('false');
  });

  it('leaves CJK and path text readable', () => {
    expect(esc('深夜動態 C:\\HeliVMSData\\a.mp4')).toBe('深夜動態 C:\\HeliVMSData\\a.mp4');
  });

  it('neutralizes a script payload', () => {
    const out = esc('<img src=x onerror=alert(1)>');
    expect(out).not.toContain('<');
    expect(out).toBe('&lt;img src=x onerror=alert(1)&gt;');
  });

  it('neutralizes attribute breakouts', () => {
    expect(esc('" onmouseover="steal()')).not.toContain('"');
  });

  it('does not double-encode existing entities twice', () => {
    expect(esc('&amp;')).toBe('&amp;amp;');
  });
});

describe('escPct', () => {
  it('keeps chart geometry numeric', () => {
    expect(escPct(12.3456)).toBe('12.3456');
    expect(escPct(0)).toBe('0');
  });

  it('falls back to zero for non-finite values', () => {
    expect(escPct(Number.NaN)).toBe('0');
    expect(escPct(undefined)).toBe('0');
  });

  it('escapes injected css', () => {
    expect(escPct(1)).not.toContain('<');
  });
});

describe('bearerHeader', () => {
  it('builds the bearer header for a key', () => {
    expect(bearerHeader('secret-key')).toEqual({ Authorization: 'Bearer secret-key' });
  });

  it('trims surrounding whitespace', () => {
    expect(bearerHeader('  secret-key  ')).toEqual({ Authorization: 'Bearer secret-key' });
  });

  it('omits the header entirely for a blank key', () => {
    expect(bearerHeader('')).toEqual({});
    expect(bearerHeader('   ')).toEqual({});
    expect(bearerHeader(null)).toEqual({});
    expect(bearerHeader(undefined)).toEqual({});
  });
});

describe('eventRow', () => {
  it('maps a forensic search hit (source/sourceId/text/occurredAtUtc)', () => {
    expect(
      eventRow({ source: 1, sourceId: 7, text: 'connection lost', occurredAtUtc: '2026-01-02T03:04:05Z', status: 'pending' }),
    ).toEqual({ id: 7, at: '2026-01-02T03:04:05Z', time: '03:04:05', source: 1, text: 'connection lost', status: 'pending' });
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

describe('即時監看分批的頻道編號', () => {
  it('分頁取出的是資料庫主鍵而不是頻道物件', () => {
    // 實機主鍵是 91/93/94 這種值；把整列送給 viewer.open 會被當成物件，
    // 角標顯示 #[object Object]·頻道須為正整數，每一格都黑畫面。
    expect(liveChannelIds([{ id: 91, name: '大門' }, { id: 93, name: '車道' }])).toEqual([91, 93]);
  });

  it('字串主鍵會轉成正整數而無效列不會被送去開播', () => {
    expect(liveChannelIds(['7', { id: '8' }, null, { id: 0 }, { id: NaN }, 9])).toEqual([7, 8, 9]);
  });

  it('沒有列時回空清單', () => {
    expect(liveChannelIds(null)).toEqual([]);
    expect(liveChannelIds([])).toEqual([]);
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

describe('filterBoardRows', () => {
  const rows = [
    { id: 1, status: 'pending' },
    { id: 2, status: 'acknowledged' },
    { id: 3, status: 'actioned', overdue: true },
    { id: 4, status: 'false_alarm' },
  ];

  it('returns everything without a filter', () => {
    expect(filterBoardRows(rows, '')).toHaveLength(4);
    expect(filterBoardRows(rows, undefined)).toHaveLength(4);
    expect(filterBoardRows(rows, null)).toHaveLength(4);
  });

  it('filters by status', () => {
    expect(filterBoardRows(rows, 'pending').map((r) => r.id)).toEqual([1]);
    expect(filterBoardRows(rows, 'acknowledged').map((r) => r.id)).toEqual([2]);
    expect(filterBoardRows(rows, 'false_alarm').map((r) => r.id)).toEqual([4]);
  });

  it('filters overdue by flag', () => {
    expect(filterBoardRows(rows, 'overdue').map((r) => r.id)).toEqual([3]);
  });

  it('tolerates non-array input and unknown filters', () => {
    expect(filterBoardRows(null, 'pending')).toEqual([]);
    expect(filterBoardRows(rows, 'nonsense')).toEqual([]);
  });
});

describe('login gating', () => {
  it('gates acting on admin role', () => {
    expect(canAct('admin')).toBe(true);
    expect(canAct('viewer')).toBe(false);
    expect(canAct(null)).toBe(false);
  });

  it('allows polling only when visible and not typing or pressing', () => {
    expect(pollGate(undefined, false)).toBe(true);
    expect(pollGate('BODY', false)).toBe(true);
    expect(pollGate('body', false)).toBe(true);
    expect(pollGate('input', false)).toBe(false);
    expect(pollGate('INPUT', false)).toBe(false);
    expect(pollGate('SELECT', false)).toBe(false);
    expect(pollGate('textarea', false)).toBe(false);
    expect(pollGate('BUTTON', false)).toBe(false);
    expect(pollGate('INPUT', true)).toBe(false);
    expect(pollGate('BODY', true)).toBe(false);
  });

  it('parses login responses', () => {
    expect(parseLogin({ role: 'admin', displayName: '操作員', token: 'sess-token-1' })).toEqual({
      ok: true,
      role: 'admin',
      displayName: '操作員',
      token: 'sess-token-1',
    });
    expect(parseLogin({ role: 'viewer' })).toEqual({
      ok: true,
      role: 'viewer',
      displayName: null,
      token: null,
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
    expect(rows.map((r) => r.id)).toEqual([1, 2]);
    expect(rows.every((r) => typeof r.id === 'number')).toBe(true);
    expect(accountRows(null)).toEqual([]);
  });

  it('normalizes config cards', () => {
    expect(configCard({ authEnabled: true, lockoutThreshold: 3, lockoutMinutes: 30 })).toEqual({
      authEnabled: true,
      lockoutThreshold: 3,
      lockoutMinutes: 30,
      recordingRetentionDays: 30,
      recordingWatermarkGb: 0,
      alarmRetentionDays: 365,
      auditRetentionDays: 365,
    });
    expect(configCard({ AuthEnabled: 1, LockoutThreshold: '4', LockoutMinutes: 15 })).toEqual({
      authEnabled: true,
      lockoutThreshold: 4,
      lockoutMinutes: 15,
      recordingRetentionDays: 30,
      recordingWatermarkGb: 0,
      alarmRetentionDays: 365,
      auditRetentionDays: 365,
    });
    expect(configCard(null)).toEqual({
      authEnabled: false,
      lockoutThreshold: 5,
      lockoutMinutes: 5,
      recordingRetentionDays: 30,
      recordingWatermarkGb: 0,
      alarmRetentionDays: 365,
      auditRetentionDays: 365,
    });
    expect(
      configCard({ recordingRetentionDays: 90, recordingWatermarkGb: '12.5', auditRetentionDays: 0 }),
    ).toMatchObject({ recordingRetentionDays: 90, recordingWatermarkGb: 12.5, auditRetentionDays: 0 });
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

  it('normalizes audit rows and filters by category/actor', () => {
    const rows = auditRows([
      { Id: 1, OccurredAtUtc: '2026-01-01T00:00:00Z', Actor: 'Admin', Action: 'settings.set', Category: 'config', Detail: 'x' },
      { id: 2, actor: 'tester', action: 'login.ok', category: 'auth' },
      { id: 3, actor: 'tester', action: 'share.create', category: 'share' },
    ]);
    expect(rows[0].category).toBe('config');
    expect(auditFilter(rows, 'auth')).toHaveLength(1);
    expect(auditFilter(rows, '', 'test')).toHaveLength(2);
    expect(auditFilter(rows, 'share', 'x')).toHaveLength(0);
    expect(auditFilter(null, '')).toEqual([]);
  });

  it('rolls up daily report cards', () => {
    const card = dailyCard({
      recording: [
        { ChannelId: 1, Hours: 1, Bytes: 1073741824 },
        { Hours: 0.5, bytes: 0 },
      ],
      capacity: [{ day: '2026-01-01', bytes: 1, hours: 1 }],
      disconnects: 2,
      events: [
        { EventType: 'motion', Count: 5 },
        { EventType: 'offline', Count: 1 },
      ],
    });
    expect(card.hours).toBe(1.5);
    expect(card.gb).toBe(1);
    expect(card.disconnects).toBe(2);
    expect(card.events[0]).toEqual({ type: 'motion', count: 5 });
    expect(dailyCard(null)).toEqual({ hours: 0, bytes: 0, gb: 0, disconnects: 0, events: [] });
  });

  it('focuses critical tiles double-size and pushes normals below', () => {
    const layout = focusLayout(
      [
        { priority: 'critical' },
        { priority: 'normal' },
        { priority: 'critical' },
        { priority: 'normal' },
      ],
      4,
    );
    expect(layout).toHaveLength(4);
    expect(layout[0]).toEqual({ left: 0, top: 0, width: 0.5, height: 0.5 });
    expect(layout[2]).toEqual({ left: 0.5, top: 0, width: 0.5, height: 0.5 });
    expect(layout[1]).toEqual({ left: 0, top: 0.5, width: 0.25, height: 0.25 });
    expect(layout[3]).toEqual({ left: 0.25, top: 0.5, width: 0.25, height: 0.25 });
    expect(focusLayout([], 4)).toEqual([]);
  });

  it('keeps row/col alignment for odd critical counts', () => {
    const layout = focusLayout([{ priority: 'critical' }, { priority: 'normal' }], 4);
    expect(layout[0]).toEqual({ left: 0, top: 0, width: 0.5, height: 0.5 });
    expect(layout[1]).toEqual({ left: 0, top: 0.5, width: 0.25, height: 0.25 });
  });

  it('labels recording schedule windows', () => {
    expect(scheduleLabel({ daysMask: 0b0111110, startMinute: 9 * 60, endMinute: 17 * 60 })).toBe(
      '一-五 09:00-17:00',
    );
    expect(scheduleLabel({ daysMask: 0, startMinute: 0, endMinute: 1439 })).toContain('無');
    expect(
      scheduleInEffect({ daysMask: 0b1000000, startMinute: 9 * 60, endMinute: 17 * 60 }, new Date(2026, 8, 26, 12)),
    ).toBe(true);
    expect(
      scheduleInEffect({ daysMask: 0b1000000, startMinute: 9 * 60, endMinute: 17 * 60 }, new Date(2026, 8, 26, 18)),
    ).toBe(false);
    expect(
      scheduleInEffect({ enabled: false, daysMask: 127, startMinute: 0, endMinute: 1439 }, new Date(2026, 8, 26, 12)),
    ).toBe(false);
  });

  it('renders patrol summaries', () => {
    expect(patrolLabel({ name: '日巡', channelId: 3, windowStart: '08:00', windowEnd: '18:00', steps: [{}, {}] })).toBe(
      '日巡 · 頻道3 · 08:00-18:00 · 2 步',
    );
    expect(patrolLabel(null)).toBe('未命名 · 頻道- · 00:00-23:59 · 0 步');
  });

  it('renders evidence rows', () => {
    expect(evRows([{ id: 1, status: 'packaged', createdAt: 't', items: 3 }])).toEqual([
      { id: 1, status: 'packaged', createdAt: 't', items: 3 },
    ]);
    expect(evRows(null)).toEqual([]);
  });

  it('renders backup rows', () => {
    expect(
      backupRows([
        { runAt: 't', copiedCount: 3, copiedBytes: 1200, failedCount: 1, checkpointUtc: null, targetRoot: '/t' },
      ]),
    ).toEqual([{ seq: 1, runAt: 't', copied: 3, bytes: 1200, failed: 1, advanced: false, target: '/t' }]);
    expect(backupRows(null)).toEqual([]);
  });

  it('renders central node rows and treats a missing enabled flag as enabled', () => {
    const rows = centralRows([
      { id: 4, name: 'child', baseUrl: 'http://child:5080', enabled: false, nodeId: 'N-1', version: '0.1.0', channels: 8, online: true, lastSeenUtc: '2026-10-13T00:00:00Z', lastError: '' },
      { id: 5, name: 'legacy', baseUrl: 'http://legacy', online: false, channels: null },
    ]);
    expect(rows[0]).toMatchObject({ id: 4, enabled: false, online: true, channels: 8 });
    expect(rows[1]).toMatchObject({ id: 5, enabled: true, online: false, channels: 0, lastSeenUtc: '' });
    expect(centralRows(null)).toEqual([]);
  });

  it('renders central channel rows and treats a missing enabled flag as enabled', () => {
    const rows = centralChannelRows([
      { nodeId: 4, nodeName: '機房 A', channelId: 1, channelName: '大門', enabled: true },
      { nodeId: 5, nodeName: '機房 B', channelId: 2, channelName: '車道' },
      { nodeId: 5, nodeName: '機房 B', channelId: 3, channelName: '後門', enabled: false },
    ]);
    expect(rows[0]).toEqual({ nodeId: 4, nodeName: '機房 A', channelId: 1, channelName: '大門', enabled: true });
    expect(rows[1]).toMatchObject({ nodeId: 5, channelId: 2, enabled: true });
    expect(rows[2].enabled).toBe(false);
    expect(centralChannelRows(null)).toEqual([]);
  });

  it('renders central alarm rows with node identity and overdue state', () => {
    const rows = centralAlarmRows([
      { nodeId: 4, nodeName: '機房 A', eventId: 41, channelId: 2, eventType: 'motion', startUtc: 'x', priority: 'high', status: 'pending', overdue: true },
      { nodeId: 5, nodeName: '機房 B', eventId: 42, channelId: 1, eventType: 'intrusion', startUtc: 'y' },
    ]);
    expect(rows[0]).toMatchObject({ nodeId: 4, nodeName: '機房 A', eventId: 41, channelId: 2, eventType: 'motion', priority: 'high', status: 'pending', overdue: true });
    // 缺 overdue/priority 一律視為未逾期、空字串，避免整格顯示 undefined。
    expect(rows[1]).toMatchObject({ nodeId: 5, eventId: 42, overdue: false, priority: '' });
    expect(centralAlarmRows(null)).toEqual([]);
    expect(centralAlarmRows(undefined)).toEqual([]);
  });

  it('renders door rows', () => {
    const rows = doorRows([
      { deviceId: 3, doorId: 2, cardId: 'C-1', direction: 'In', granted: true, reason: 'ok', occurredAtUtc: 'x' },
    ]);
    expect(rows[0]).toMatchObject({ device: 3, door: 2, card: 'C-1', direction: 'In', granted: true, reason: 'ok' });
    expect(doorRows(null)).toEqual([]);
  });

  it('renders detection rows', () => {
    const rows = detRows([{ channelId: 1, class: 'person', confidence: 0.95, x: 0.1, y: 0.2, w: 0.3, h: 0.4, detectedUtc: 'x' }]);
    expect(rows[0]).toMatchObject({ channel: 1, cls: 'person', conf: 0.95, x: 0.1, y: 0.2, box: '0.3×0.4' });
    expect(detRows(null)).toEqual([]);
  });

  it('renders notification rows', () => {
    const rows = notifRows([{ channelId: 2, eventType: 'motion', route: 'webhook', ok: true, attempts: 1, detail: 'ok', tsUtc: 'x' }]);
    expect(rows[0]).toMatchObject({ channel: 2, event: 'motion', route: 'webhook', ok: true, attempts: 1, detail: 'ok' });
    expect(notifRows(null)).toEqual([]);
  });

  it('renders export rows', () => {
    const rows = exportRows([
      { id: 3, channelId: 1, stream: 'main', startUtc: 'x', endUtc: 'y', status: 'queued', fileSizeBytes: null, sha256: 'abc', error: '' },
    ]);
    expect(rows[0]).toMatchObject({ id: 3, channel: 1, stream: 'main', status: 'queued', sha: 'abc' });
    expect(exportRows(null)).toEqual([]);
  });

  it('renders provider rows', () => {
    const rows = providerRows([
      { id: 2, name: 'corp-ad', kind: 'ldap', enabled: false, configJson: '{}', createdAt: 't' },
    ]);
    expect(rows[0]).toMatchObject({ id: 2, name: 'corp-ad', kind: 'ldap', enabled: false, config: '{}' });
    expect(providerRows(null)).toEqual([]);
  });

  it('renders legal hold rows', () => {
    const rows = holdRows([
      { id: 5, channelId: 1, fromUtc: 'x', toUtc: 'y', reason: '案件', createdBy: 'auditor', active: true, revokedBy: null },
    ]);
    expect(rows[0]).toMatchObject({ id: 5, channel: 1, reason: '案件', by: 'auditor', active: true, revokedBy: '' });
    expect(holdRows(null)).toEqual([]);
  });

  it('renders alert rule rows', () => {
    const rows = ruleRows([
      { id: 2, name: '夜間動態', eventType: 'motion', channelId: 2, keyword: null, channels: null, enabled: false, minEventsInWindow: 2 },
    ]);
    expect(rows[0]).toMatchObject({ id: 2, name: '夜間動態', event: 'motion', channel: 2, enabled: false, min: 2 });
    expect(ruleRows(null)).toEqual([]);
  });

  it('renders share rows', () => {
    const rows = shareRows([
      { id: 1, token: 'abcdef1234567890', kind: 'segment', resourcePath: '/tmp/x.mp4', label: '深夜', createdBy: null, useCount: 1, maxUses: 3, active: true },
    ]);
    expect(rows[0]).toMatchObject({ id: 1, token: 'abcdef123456', kind: 'segment', uses: '1/3', active: true });
    expect(shareRows(null)).toEqual([]);
  });

  it('renders redaction rows', () => {
    const rows = redRows([
      { id: 4, sourceType: 'clip', refId: 9, channelId: 1, occurredAtUtc: 'x', x: 10, y: 20, width: 50, height: 60, filled: true },
    ]);
    expect(rows[0]).toMatchObject({ id: 4, source: 'clip', ref: 9, channel: 1, rect: '10,20 50×60', filled: true });
    expect(redRows(null)).toEqual([]);
  });

  it('renders replication rows', () => {
    const rows = repRows([
      { id: 1, sourcePath: 'C:\\src', destinationPath: 'D:\\dst', intervalMinutes: 60, enabled: true, lastResult: 'FAILED', lastError: '複製失敗 1 檔', consecutiveFailures: 2, due: true },
    ]);
    expect(rows[0]).toMatchObject({ id: 1, minutes: 60, enabled: true, lastResult: 'FAILED', fails: 2, due: true });
    expect(repRows(null)).toEqual([]);
  });

  it('renders alarm board rows', () => {
    const rows = alarmRows([
      { eventId: 7, channelId: 2, eventType: 'motion', startUtc: 'x', status: 'pending', priority: 'high', owner: 'ops', overdue: true },
    ]);
    expect(rows[0]).toMatchObject({ id: 7, channel: 2, event: 'motion', status: 'pending', priority: 'high', overdue: true });
    expect(alarmRows(null)).toEqual([]);
  });

  it('renders system metric rows', () => {
    const rows = sysMetricsRows({
      capturedAtUtc: 'x', uptimeMinutes: 3, workingSetMb: 40, managedHeapMb: 8, cpuPercent: 12.5,
      disks: [{ name: 'C:', totalMb: 120, freeMb: 80, format: 'NTFS' }],
    });
    expect(rows).toMatchObject({
      captured: formatTimestamp('x'),
      uptimeMinutes: 3,
      workingSetMb: 40,
      managedHeapMb: 8,
      cpuPercent: 12.5,
      disks: [{ name: 'C:', totalMb: 120, freeMb: 80, format: 'NTFS' }],
    });
    expect(sysMetricsRows(null)).toEqual(expect.objectContaining({ disks: [] }));
  });

  it('renders memory trend rows sorted ascending', () => {
    const rows = sysTrendRows([
      { capturedAtUtc: '2026-01-01T00:02:00Z', workingSetGb: 2 },
      { capturedAtUtc: '2026-01-01T00:01:00Z', workingSetGb: 1 },
      { capturedAtUtc: '2026-01-01T00:03:00Z', workingSetGb: 3 },
    ]);
    expect(rows.map((r) => r.workingSetGb)).toEqual([1, 2, 3]);
    expect(rows[0].captured).toBe(formatTimestamp('2026-01-01T00:01:00Z'));
    expect(sysTrendRows(null)).toEqual([]);
    expect(sysTrendRows(undefined)).toEqual([]);
  });

  it('renders disk capacity trend rows sorted ascending', () => {
    const rows = sysDiskTrendRows([
      { capturedAtUtc: '2026-01-01T00:02:00Z', name: 'C:', freeMb: 40, totalMb: 120 },
      { capturedAtUtc: '2026-01-01T00:01:00Z', name: 'C:', freeMb: 80, totalMb: 120 },
    ]);
    expect(rows.map((r) => r.freeMb)).toEqual([80, 40]);
    expect(rows[0].name).toBe('C:');
    expect(rows[0].totalMb).toBe(120);
    expect(sysDiskTrendRows(null)).toEqual([]);
    expect(sysDiskTrendRows(undefined)).toEqual([]);
  });

  it('renders clip search hits sorted by score desc', () => {
    const rows = clipViewRows([
      { refId: 1, sourceType: 'event', label: 'tamper', score: 0.2 },
      { refId: 2, sourceType: 'event', label: 'motion', score: 0.9 },
    ]);
    expect(rows.map((r) => r.refId)).toEqual([2, 1]);
    expect(rows[0].label).toBe('motion');
    expect(clipViewRows(null)).toEqual([]);
    expect(clipViewRows(undefined)).toEqual([]);
  });

  it('renders fuse results sorted by score desc', () => {
    const rows = fuseViewRows([
      { key: 'alarm:1', score: 0.3 },
      { key: 'alarm:2', score: 0.9 },
    ]);
    expect(rows.map((r) => r.key)).toEqual(['alarm:2', 'alarm:1']);
    expect(fuseViewRows(null)).toEqual([]);
    expect(fuseViewRows(undefined)).toEqual([]);
  });

  it('assigns smartwall tiles row-major by rank', () => {
    const cells = [
      { channelId: 1, rank: 3, highlight: false },
      { channelId: 2, rank: 1, highlight: true },
      { channelId: 3, rank: 2 },
    ];
    const laid = smartwallLayout(cells, { cols: 2 });
    expect(laid.map((t) => t.channelId)).toEqual([2, 3, 1]);
    expect(laid[0]).toMatchObject({ row: 1, col: 1 });
    expect(laid[1]).toMatchObject({ row: 1, col: 2 });
    expect(laid[2]).toMatchObject({ row: 2, col: 1 });
  });

  it('truncates smartwall tiles beyond capacity and guards null', () => {
    const cells = Array.from({ length: 10 }, (_, i) => ({ channelId: i + 1, rank: i + 1 }));
    const laid = smartwallLayout(cells, { cols: 2, rows: 2 });
    expect(laid).toHaveLength(4);
    expect(smartwallLayout(null, { cols: 4 })).toEqual([]);
    expect(smartwallLayout(undefined, { cols: 4 })).toEqual([]);
  });

  it('maps audio items to flat rows sorted by event id', () => {
    const rows = audioRows([
      { eventId: 7, channelId: 2, durationMs: 1000, mime: 'audio/ogg' },
      { eventId: 3, channelId: 1, durationMs: 500, mime: 'audio/wav' },
    ]);
    expect(rows.map((r) => r.eventId)).toEqual([3, 7]);
    expect(rows[1]).toMatchObject({ channelId: 2, durationMs: 1000, mime: 'audio/ogg' });
    expect(audioRows(null)).toEqual([]);
    expect(audioRows(undefined)).toEqual([]);
  });
});
describe('parseM3u8', () => {
  const playlist = [
    '#EXTM3U',
    '#EXT-X-VERSION:7',
    '#EXT-X-PLAYLIST-TYPE:VOD',
    '#EXT-X-TARGETDURATION:15',
    '#EXT-X-MAP:URI="/api/stream/segment/12/init.mp4"',
    '#EXT-X-PROGRAM-DATE-TIME:2026-09-30T12:00:00.000Z',
    '#EXTINF:15.000,',
    '/api/stream/segment/12/media.m4s',
    '#EXT-X-PROGRAM-DATE-TIME:2026-09-30T12:00:15.000Z',
    '#EXTINF:10.500,',
    '/api/stream/segment/13/media.m4s',
    '#EXT-X-ENDLIST',
  ].join('\n');

  it('reads the init segment, durations and program date times', () => {
    const parsed = parseM3u8(playlist);
    expect(parsed.initUri).toBe('/api/stream/segment/12/init.mp4');
    expect(parsed.targetDuration).toBe(15);
    expect(parsed.segments).toHaveLength(2);
    expect(parsed.segments[0]).toMatchObject({ uri: '/api/stream/segment/12/media.m4s', duration: 15 });
    expect(parsed.segments[1]).toMatchObject({ uri: '/api/stream/segment/13/media.m4s', duration: 10.5 });
    expect(parsed.duration).toBeCloseTo(25.5, 5);
    expect(parsed.segments[1].start).toBe(Date.parse('2026-09-30T12:00:15.000Z'));
  });

  it('returns null for anything that is not a playlist', () => {
    expect(parseM3u8('')).toBeNull();
    expect(parseM3u8('<html>404</html>')).toBeNull();
    expect(parseM3u8(undefined)).toBeNull();
  });

  it('tolerates CRLF and a playlist with no segments', () => {
    const crlf = '#EXTM3U\r\n#EXT-X-PLAYLIST-TYPE:VOD\r\n#EXT-X-ENDLIST\r\n';
    const parsed = parseM3u8(crlf);
    expect(parsed.initUri).toBeNull();
    expect(parsed.segments).toEqual([]);
    expect(parsed.duration).toBe(0);
  });

  it('never invents a duration for malformed EXTINF values', () => {
    const parsed = parseM3u8('#EXTM3U\n#EXTINF:not-a-number,\n/a.m4s');
    expect(parsed.segments[0].duration).toBe(0);
    expect(parsed.targetDuration).toBe(0);
  });
});

/**
 * fMP4 測試素材在測試裡現做：盒子只有 size＋type＋內容，幾個 u32 就能拼出
 * 解析器認得的最小 moov／moof。與其塞二進位檔進 repo，不如讓結構在這裡看得見——
 * 解析器認的就是這些欄位（tkhd 的 track_id、mdhd 的 timescale、hdlr 的 handler、
 * tfdt 的起點、trun 的樣本長度），換任何一個欄位的偏移都該讓這裡轉紅。
 */
function mp4Box(type, ...payload) {
  const body = Buffer.concat(payload);
  const header = Buffer.alloc(8);
  header.writeUInt32BE(body.length + 8, 0);
  header.write(type, 4, 'ascii');
  return Buffer.concat([header, body]);
}

function mp4U32(value) {
  const b = Buffer.alloc(4);
  b.writeUInt32BE(value >>> 0, 0);
  return b;
}

function mp4U24(value) {
  const b = Buffer.alloc(3);
  b.writeUIntBE(value, 0, 3);
  return b;
}

/** 初始化段：trak 1 是影片軌（handler=vide、timescale=1000）。 */
function initSegmentBytes() {
  const tkhd = mp4Box('tkhd', Buffer.concat([Buffer.alloc(4), mp4U32(0), mp4U32(0), mp4U32(1)]));
  const mdhd = mp4Box('mdhd', Buffer.concat([Buffer.alloc(4), mp4U32(0), mp4U32(0), mp4U32(1000)]));
  const hdlr = mp4Box('hdlr', Buffer.concat([Buffer.alloc(4), Buffer.alloc(4), Buffer.from('vide')]));
  const mdia = mp4Box('mdia', mdhd, hdlr);
  const trak = mp4Box('trak', tkhd, mdia);
  return Buffer.concat([
    mp4Box('ftyp', Buffer.from('isom'), mp4U32(0), Buffer.from('isomiso2')),
    mp4Box('moov', trak),
  ]);
}

/** 媒體段：單一 moof/traf，trun 逐樣本帶長度，樣本長度總和＝影片軌跨度。 */
function mediaSegmentBytes({ base, durations, trackId = 1 }) {
  const tfhd = mp4Box('tfhd', Buffer.concat([Buffer.alloc(4), mp4U32(trackId)]));
  const tfdt = mp4Box('tfdt', Buffer.concat([Buffer.alloc(4), mp4U32(base)]));
  const trun = mp4Box(
    'trun',
    Buffer.concat([
      Buffer.concat([Buffer.from([0]), mp4U24(0x000100)]),
      mp4U32(durations.length),
      ...durations.map(mp4U32),
    ]),
  );
  const traf = mp4Box('traf', tfhd, tfdt, trun);
  const mfhd = mp4Box('mfhd', Buffer.concat([Buffer.alloc(4), mp4U32(1)]));
  const moof = mp4Box('moof', mfhd, traf);
  return Buffer.concat([moof, mp4Box('mdat', Buffer.alloc(8))]);
}

describe('parseFmp4InitTracks', () => {
  it('讀取初始化段的軌道編號、時間刻度與 handler', () => {
    const tracks = parseFmp4InitTracks(initSegmentBytes());
    expect(tracks).toEqual([{ id: 1, timescale: 1000, handler: 'vide' }]);
  });

  it('不是初始化段的東西回 null 而不是擲例外', () => {
    expect(parseFmp4InitTracks(Buffer.from('<html>404</html>'))).toBeNull();
    expect(parseFmp4InitTracks(Buffer.alloc(0))).toBeNull();
    expect(parseFmp4InitTracks(undefined)).toBeNull();
  });
});

describe('parseFmp4VideoSpan', () => {
  const tracks = [{ id: 1, timescale: 1000, handler: 'vide' }];

  it('以 tfdt 與 trun 量出影片軌在該段的時間跨度（秒）', () => {
    const span = parseFmp4VideoSpan(tracks, mediaSegmentBytes({ base: 0, durations: [400, 600] }));
    expect(span).toEqual({ start: 0, end: 1 });
  });

  it('起點取自 tfdt，多個 moof 取最寬的包絡', () => {
    const bytes = Buffer.concat([
      mediaSegmentBytes({ base: 250, durations: [400] }),
      mediaSegmentBytes({ base: 650, durations: [350] }),
    ]);
    expect(parseFmp4VideoSpan(tracks, bytes)).toEqual({ start: 0.25, end: 1 });
  });

  it('量不到影片軌就回 null（接縫與時鐘退回牆鐘估算）', () => {
    expect(parseFmp4VideoSpan(tracks, mediaSegmentBytes({ base: 0, durations: [400], trackId: 9 }))).toBeNull();
    expect(parseFmp4VideoSpan([], mediaSegmentBytes({ base: 0, durations: [400] }))).toBeNull();
    expect(parseFmp4VideoSpan(null, mediaSegmentBytes({ base: 0, durations: [400] }))).toBeNull();
  });

  it('毀損或空跨度的位元組回 null 而不是擲例外', () => {
    expect(parseFmp4VideoSpan(tracks, mediaSegmentBytes({ base: 0, durations: [] }))).toBeNull();
    expect(parseFmp4VideoSpan(tracks, Buffer.alloc(0))).toBeNull();
    expect(parseFmp4VideoSpan(tracks, Buffer.from('not fmp4 at all!!'))).toBeNull();
  });
});

describe('buildRecordingClock', () => {
  const T = Date.parse('2026-10-20T14:00:00Z');

  it('段內位置按比例插值成該段的錄影牆鐘', () => {
    // 牆鐘 62 秒、媒體 60 秒（ffmpeg 啟動與收尾被算進牆鐘）：段中點顯示的是
    // 牆鐘的 31 秒處，不是媒體的 30 秒處。
    const clock = buildRecordingClock([
      { mediaStart: 0, mediaEnd: 60, wallStartMs: T, wallDurS: 62 },
    ]);
    expect(clock.at(30)).toBe(T + 31 * 1000);
    expect(clock.at(0)).toBe(T);
    expect(clock.at(60)).toBe(T + 62 * 1000);
  });

  it('段界連續：牆鐘與媒體長度不同時也不會跳回', () => {
    // 罪狀版的算法（起點＋經過秒數）會在段界倒退一個暖機量級；這裡前後兩側
    // 必須落在同一個牆鐘刻度上，操作員跨段追事件時時間才不會往回跑。
    const clock = buildRecordingClock([
      { mediaStart: 0, mediaEnd: 60, wallStartMs: T, wallDurS: 62 },
      { mediaStart: 60, mediaEnd: 120, wallStartMs: T + 62_000, wallDurS: 61 },
    ]);
    expect(clock.at(60)).toBe(T + 62_000);
    expect(Math.abs(clock.at(59.999) - clock.at(60))).toBeLessThan(50);
    expect(Math.abs(clock.at(60.001) - clock.at(60))).toBeLessThan(50);
  });

  it('沒有 PDT 的段不進時鐘（顯示不出依據時間時寧可留白）', () => {
    const clock = buildRecordingClock([
      { mediaStart: 0, mediaEnd: 60, wallStartMs: null, wallDurS: 62 },
    ]);
    expect(clock.at(30)).toBeNull();
    expect(buildRecordingClock([]).at(30)).toBeNull();
    expect(buildRecordingClock(null).at(30)).toBeNull();
    expect(buildRecordingClock([{ mediaStart: 0, mediaEnd: 0, wallStartMs: T }]).at(0)).toBeNull();
  });

  it('游標在時間軸外時夾到最近端點', () => {
    const clock = buildRecordingClock([
      { mediaStart: 0, mediaEnd: 60, wallStartMs: T, wallDurS: 60 },
    ]);
    expect(clock.at(-5)).toBe(T);
    expect(clock.at(9999)).toBe(T + 60 * 1000);
    expect(clock.at(Number.NaN)).toBeNull();
  });
});

describe('playbackRangesToEvict', () => {
  it('播放頭後方的舊資料會被丟掉，正在播與還沒播的一律留著', () => {
    // SourceBuffer 的配額固定：整份 40 分鐘清單一次 append 會得到
    // 「The SourceBuffer is full」而黑屏（實機症狀），所以邊播邊補時必須邊丟。
    const ranges = [{ start: 0, end: 300 }];
    expect(playbackRangesToEvict(ranges, 200, 30)).toEqual([{ start: 0, end: 170 }]);
  });

  it('還在保留範圍內時不丟任何東西', () => {
    expect(playbackRangesToEvict([{ start: 0, end: 60 }], 30, 30)).toEqual([]);
    expect(playbackRangesToEvict([], 500, 30)).toEqual([]);
    expect(playbackRangesToEvict(null, 500, 30)).toEqual([]);
  });

  it('緩衝尾端在保留線之前時整段丟掉', () => {
    expect(playbackRangesToEvict([{ start: 0, end: 60 }], 300, 30)).toEqual([{ start: 0, end: 60 }]);
  });
});

describe('playbackNeedsSegment', () => {
  it('完全沒有緩衝時要補第一段', () => {
    expect(playbackNeedsSegment([], 0)).toBe(true);
    expect(playbackNeedsSegment(null, 0)).toBe(true);
  });

  it('播放頭接近緩衝尾端才補下一段，不是一次抓完整份清單', () => {
    // 這正是「SourceBuffer is full」的來源：舊版把整份清單 append 完才開播。
    expect(playbackNeedsSegment([{ start: 0, end: 300 }], 0)).toBe(false);
    expect(playbackNeedsSegment([{ start: 0, end: 300 }], 200)).toBe(false);
    expect(playbackNeedsSegment([{ start: 0, end: 300 }], 260)).toBe(true);
  });

  it('一次補很多段時以最後一段的尾端為準', () => {
    expect(
      playbackNeedsSegment([{ start: 0, end: 300 }, { start: 300, end: 600 }], 550),
    ).toBe(true);
    expect(
      playbackNeedsSegment([{ start: 0, end: 300 }, { start: 300, end: 600 }], 480),
    ).toBe(false);
  });

  it('不會因為播放頭落在緩衝範圍外就一路補到清單結尾', () => {
    // 這一條是「照順序補」與「跳到那一段」的分界：播放頭在最後一段後面 80 分鐘時，
    // 尾端判斷必須是 false——真正的處置是跳到播放頭所在的那一段（呼叫端負責），
    // 若這裡回 true，往回拖就會把整份清單重新抓一遍，正是爆配額的老問題。
    expect(playbackNeedsSegment([{ start: 7770, end: 8113 }], 974)).toBe(false);
  });
});

describe('playbackRangesAheadToEvict', () => {
  it('跳段後把播放頭前方很遠的緩衝丟掉，往回拖才不會留下整份清單', () => {
    // 實機症狀：在同一個 3 小時窗裡來回拖，之前跳過的時段一直留在 SourceBuffer，
    // 幾次之後就撞上「The SourceBuffer is full」而畫面停在錯誤訊息。
    expect(playbackRangesAheadToEvict([{ start: 7770, end: 8113 }], 974, 60))
      .toEqual([{ start: 7770, end: 8113 }]);
  });

  it('播放頭附近與後方的資料留著，才不會為了跳一段而丟掉正在播的內容', () => {
    expect(playbackRangesAheadToEvict([{ start: 900, end: 1200 }], 974, 60)).toEqual([]);
    // 300 秒的段會超出 lookahead，但它從播放頭之前就開始，整段都要留。
    expect(playbackRangesAheadToEvict([{ start: 0, end: 900 }], 10, 60)).toEqual([]);
    expect(playbackRangesAheadToEvict([], 500, 60)).toEqual([]);
    expect(playbackRangesAheadToEvict(null, Number.NaN, 60)).toEqual([]);
  });
});

describe('playbackBufferedFor', () => {
  const entry = { mediaStart: 0, mediaEnd: 300 };

  it('資料還在緩衝區裡時說「有」，跳段與收斂時長才不會互相打架', () => {
    // 播放頭跑到整條時間軸尾端時，最後一段其實還在緩衝區：那時該做的是把 duration
    // 收斂到真實結尾，而不是每一輪重新 append 最後一段。
    expect(playbackBufferedFor([{ start: 0, end: 300 }], entry)).toBe(true);
    expect(playbackBufferedFor([{ start: 0, end: 320 }], entry)).toBe(true);
  });

  it('被淘汰、只留半段或查不到這一段時說「沒有」，才會重抓', () => {
    expect(playbackBufferedFor([{ start: 120, end: 300 }], entry)).toBe(false);
    expect(playbackBufferedFor([{ start: 300, end: 600 }], entry)).toBe(false);
    expect(playbackBufferedFor([], entry)).toBe(false);
    expect(playbackBufferedFor([{ start: 0, end: 300 }], undefined)).toBe(false);
    expect(playbackBufferedFor([{ start: 0, end: 300 }], { mediaStart: 5, mediaEnd: 5 })).toBe(false);
  });
});

describe('playlistMediaStarts', () => {
  it('以 EXTINF 累計出每一段在時間軸上的起點', () => {
    expect(playlistMediaStarts([{ duration: 300 }, { duration: 60 }, { duration: 15 }]))
      .toEqual([0, 300, 360]);
  });

  it('缺長度的段沿用前一段，不會讓後面的段全部疊在同一點', () => {
    // 索引偶爾有 duration 寫壞的段；若當成 0，後面每一段的累計位置都會塌回同一個時刻，
    // 拖曳就永遠指到錯的段，而畫面看起來只是「跳錯地方」。
    expect(playlistMediaStarts([{ duration: 30 }, { duration: null }, { duration: 30 }]))
      .toEqual([0, 30, 60]);
    expect(playlistMediaStarts([{}, { duration: 10 }])).toEqual([0, 0]);
    expect(playlistMediaStarts(null)).toEqual([]);
  });
});

describe('playlistSegmentIndexAt', () => {
  const segments = [{ duration: 300 }, { duration: 300 }, { duration: 300 }];

  it('拖到某個時刻就指出那一段，不必從頭逐段往前補', () => {
    // 實機症狀（3 小時窗拖到 80%）：舊寫法只能從讀取頭一段一段補，補到第 10 次 append 時
    // SourceBuffer 卡在 PARSING_MEDIA_SEGMENT，畫面停在錯誤訊息；而 24 段的窗本來就該
    // 直接跳到播放頭所在的那一段。
    expect(playlistSegmentIndexAt(segments, 0)).toBe(0);
    expect(playlistSegmentIndexAt(segments, 450)).toBe(1);
    expect(playlistSegmentIndexAt(segments, 720)).toBe(2);
  });

  it('段尾那一刻屬於下一段（半開區間），與桌面回放同一種落點', () => {
    expect(playlistSegmentIndexAt(segments, 300)).toBe(1);
    expect(playlistSegmentIndexAt(segments, 600)).toBe(2);
  });

  it('超出尾端回最後一段，空清單回 null，壞值回第一段', () => {
    expect(playlistSegmentIndexAt(segments, 5000)).toBe(2);
    expect(playlistSegmentIndexAt([], 10)).toBeNull();
    expect(playlistSegmentIndexAt(segments, Number.NaN)).toBe(0);
    expect(playlistSegmentIndexAt(segments, -5)).toBe(0);
  });
});

describe('playbackSeamStart', () => {
  it('EXTINF 比檔案長一點時從上一段結尾接上去，不留下沒有資料的洞', () => {
    // 舊索引的 EXTINF 是牆鐘（每段多 0.6–12 秒），照它定位會在每個接縫留洞，
    // 瀏覽器播到洞邊就停住，看起來是「播到一半卡住」。
    expect(playbackSeamStart(600, 597.5)).toBe(597.5);
    expect(playbackSeamStart(3600, 3599.9)).toBe(3599.9);
  });

  it('真的錄影中斷（大洞）要保留，時間軸與時鐘才不會騙人', () => {
    expect(playbackSeamStart(7200, 300)).toBe(7200);
    expect(playbackSeamStart(7200, 0)).toBe(7200);
  });

  it('第一段與壞值不會被推到負數', () => {
    expect(playbackSeamStart(0, 0)).toBe(0);
    expect(playbackSeamStart(Number.NaN, 120)).toBe(120);
    expect(playbackSeamStart(-5, -1)).toBe(0);
  });
});

describe('formatLocalClock', () => {
  it('以本地時間輸出 yyyy-MM-dd HH:mm:ss', () => {
    const local = new Date(2026, 9, 20, 14, 32, 5);
    expect(formatLocalClock(local.getTime())).toBe('2026-10-20 14:32:05');
  });

  it('無效輸入回空字串，畫面上不出現 NaN 年份', () => {
    expect(formatLocalClock(Number.NaN)).toBe('');
    expect(formatLocalClock(undefined)).toBe('');
    expect(formatLocalClock(null)).toBe('');
  });
});

describe('buildPlaylistQuery', () => {
  it('builds the channel/stream/window playlist URL', () => {
    const url = buildPlaylistQuery({
      channelId: 3,
      stream: 'main',
      from: '2026-09-30T12:00:00Z',
      to: '2026-09-30T13:00:00Z',
    });
    expect(url.startsWith('/api/stream/3/playlist.m3u8?')).toBe(true);
    const params = new URLSearchParams(url.split('?')[1]);
    expect(params.get('stream')).toBe('main');
    expect(params.get('from')).toBe('2026-09-30T12:00:00.000Z');
    expect(params.get('to')).toBe('2026-09-30T13:00:00.000Z');
  });

  it('keeps the advertised window limit in sync with the server side 24 hours', () => {
    expect(PLAYLIST_WINDOW_LIMIT_S).toBe(86400);
  });
});

describe('buildCentralPlaylistQuery', () => {
  it('routes the playlist through the upper NVR proxy for the chosen child node', () => {
    const url = buildCentralPlaylistQuery({
      nodeId: 7,
      channelId: 3,
      stream: 'main',
      from: '2026-09-30T12:00:00Z',
      to: '2026-09-30T13:00:00Z',
    });
    expect(url.startsWith('/api/central/playback/7/playlist.m3u8?')).toBe(true);
    const params = new URLSearchParams(url.split('?')[1]);
    expect(params.get('channelId')).toBe('3');
    expect(params.get('stream')).toBe('main');
    expect(params.get('from')).toBe('2026-09-30T12:00:00.000Z');
    expect(params.get('to')).toBe('2026-09-30T13:00:00.000Z');
  });
});

describe('mseMimeType', () => {
  it('prefers the highest quality codec the browser claims to play', () => {
    expect(mseMimeType(true, true)).toContain('avc1.64001f');
    expect(mseMimeType(false, true)).toContain('avc1.42E01E');
    expect(mseMimeType(false, false)).toBe('video/mp4');
  });
});
