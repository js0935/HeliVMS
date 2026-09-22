import { describe, expect, it } from 'vitest';
import {
  buildSegmentsQuery,
  buildTimelineQuery,
  eventRow,
  formatTimestamp,
  gapLabel,
  parseWsMessage,
  priorityRank,
  sortBoard,
  summarizeBoard,
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