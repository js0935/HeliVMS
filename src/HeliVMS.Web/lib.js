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

export function gridLayout(count, cols = 4) {
  const n = Math.max(0, count);
  const c = Math.max(1, cols);
  const rows = Math.max(1, Math.ceil(n / c));
  return Array.from({ length: n }, (_, i) => ({
    left: (i % c) / c,
    top: Math.floor(i / c) / rows,
    width: 1 / c,
    height: 1 / rows,
  }));
}

export function pinStyles(pin, inset = 0.03) {
  const safe = pin ?? { left: 0, top: 0, width: 1, height: 1 };
  const left = (safe.left ?? 0) + inset;
  const top = (safe.top ?? 0) + inset;
  const width = Math.max((safe.width ?? 1) - inset * 2, 0);
  const height = Math.max((safe.height ?? 1) - inset * 2, 0);
  return {
    left: `${(left * 100).toFixed(2)}%`,
    top: `${(top * 100).toFixed(2)}%`,
    width: `${(width * 100).toFixed(2)}%`,
    height: `${(height * 100).toFixed(2)}%`,
  };
}

export const SMARTWALL_KEEP_MS = 300_000;

export function smartwallSnapshot(cells, now = Date.now()) {
  const list = (cells ?? []).filter((c) => {
    const t = Date.parse(c.startUtc ?? c.StartUtc);
    return !Number.isNaN(t) && now - t <= SMARTWALL_KEEP_MS;
  });
  const sorted = [...list].sort(
    (a, b) => Number(Boolean(b.highlight ?? b.Highlight)) - Number(Boolean(a.highlight ?? a.Highlight)),
  );
  return {
    cells: sorted.slice(0, 64),
    count: list.length,
    critical: list.filter((c) => (c.priority ?? c.Priority) === 'critical').length,
  };
}

export function ackRate(rows) {
  const list = rows ?? [];
  if (list.length === 0) return 0;
  const acked = list.filter((r) => (r.status ?? r.Status) === 'acknowledged').length;
  return Math.round((acked / list.length) * 100);
}

export function posTotals(records) {
  const list = records ?? [];
  const registers = {};
  for (const r of list) {
    const id = r.registerId ?? r.RegisterId ?? '?';
    registers[id] = registers[id] ?? { count: 0, totalCents: 0 };
    registers[id].count += 1;
    registers[id].totalCents += r.amountCents ?? r.AmountCents ?? 0;
  }
  return {
    count: list.length,
    totalCents: list.reduce((s, r) => s + (r.amountCents ?? r.AmountCents ?? 0), 0),
    registers,
  };
}

export function ackPayload(id, acknowledged = true) {
  return { id, body: { acknowledged } };
}

export function triagePayload(id, priority, dueUtc = null) {
  return { id, body: { priority, dueUtc, owner: null } };
}

export function canAct(role) {
  return role === 'admin';
}

export function parseLogin(payload) {
  if (!payload || typeof payload !== 'object') return { ok: false, error: '回應異常' };
  if (typeof payload.role === 'string' && payload.role.length > 0) {
    return { ok: true, role: payload.role, displayName: payload.displayName ?? null };
  }
  return { ok: false, error: payload.error || '登入失敗' };
}

export function accountRows(list) {
  return (list ?? [])
    .map((a) => ({
      id: a.id ?? a.Id,
      username: a.username ?? a.Username ?? '',
      role: a.role ?? a.Role ?? 'viewer',
      displayName: a.displayName ?? a.DisplayName ?? null,
      enabled: Boolean(a.enabled ?? a.Enabled),
      locked: Boolean(a.locked ?? a.Locked),
    }))
    .sort((a, b) => a.username.localeCompare(b.username));
}

export function configCard(raw) {
  const r = raw ?? {};
  return {
    authEnabled: Boolean(r.authEnabled ?? r.AuthEnabled ?? false),
    lockoutThreshold: Number(r.lockoutThreshold ?? r.LockoutThreshold ?? 5),
    lockoutMinutes: Number(r.lockoutMinutes ?? r.LockoutMinutes ?? 5),
  };
}

export function tileClass(cell) {
  const c = cell ?? {};
  const highlight = Boolean(c.highlight ?? c.Highlight);
  if (highlight) return 'tile-hot';
  return (c.priority ?? c.Priority ?? 'normal') === 'critical' ? 'tile-crit' : 'tile-norm';
}

export function tileLabel(cell) {
  const c = cell ?? {};
  return {
    channel: c.channelId ?? c.ChannelId ?? '?',
    type: c.eventType ?? c.EventType ?? '',
    priority: c.priority ?? c.Priority ?? 'normal',
  };
}