export const PRIORITY_RANK = { critical: 4, high: 3, normal: 2, low: 1 };

export function formatTimestamp(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toISOString().slice(11, 19);
}

export function eventRow(e) {
  return {
    id: e.id ?? e.Id,
    time: formatTimestamp(e.startUtc ?? e.StartUtc),
    channel: e.channelId ?? e.ChannelId,
    type: e.eventType ?? e.EventType,
    status: e.status ?? e.Status,
  };
}

export function priorityRank(priority) {
  return PRIORITY_RANK[priority] ?? 0;
}

export function sortBoard(rows) {
  return [...rows].sort(
    (a, b) =>
      priorityRank(b.priority) - priorityRank(a.priority) ||
      String(b.eventId ?? b.EventId ?? 0) - String(a.eventId ?? a.EventId ?? 0),
  );
}

export function summarizeBoard(rows, now = Date.now()) {
  const list = rows ?? [];
  return {
    total: list.length,
    critical: list.filter((r) => (r.priority ?? r.Priority) === 'critical').length,
    overdue: list.filter((r) => {
      const due = r.dueUtc ?? r.DueUtc;
      return due && new Date(due).getTime() < now;
    }).length,
  };
}

export function buildTimelineQuery({ channelId, stream, day }) {
  const params = new URLSearchParams({
    channelId: String(channelId),
    stream,
    day: new Date(day).toISOString(),
  });
  return `/api/recording/timeline?${params.toString()}`;
}

export function buildSegmentsQuery({ channelId, stream, from, to }) {
  const params = new URLSearchParams({
    channelId: String(channelId),
    stream,
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
  });
  return `/api/recording/segments?${params.toString()}`;
}

export function parseWsMessage(raw) {
  let message;
  try {
    message = JSON.parse(raw);
  } catch {
    return null;
  }
  if (!message || typeof message !== 'object') return null;
  const kind = message.kind ?? message.Kind;
  const eventId = message.eventId ?? message.EventId;
  if (typeof kind !== 'string' || typeof eventId !== 'number') return null;
  return {
    kind,
    eventId,
    status: message.status ?? message.Status ?? null,
    priority: message.priority ?? message.Priority ?? null,
  };
}

export function gapLabel(gapFraction) {
  if (gapFraction >= 0.999) return '無錄影';
  if (gapFraction <= 0.001) return '全日';
  return `缺 ${(gapFraction * 100).toFixed(0)}%`;
}