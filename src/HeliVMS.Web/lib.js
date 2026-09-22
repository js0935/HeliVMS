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

export function auditRows(list) {
  return (list ?? []).map((a) => ({
    id: a.id ?? a.Id,
    occurredAtUtc: a.occurredAtUtc ?? a.OccurredAtUtc ?? null,
    actor: a.actor ?? a.Actor ?? '',
    action: a.action ?? a.Action ?? '',
    category: a.category ?? a.Category ?? '',
    targetType: a.targetType ?? a.TargetType ?? null,
    targetId: a.targetId ?? a.TargetId ?? null,
    detail: a.detail ?? a.Detail ?? null,
  }));
}

export function auditFilter(rows, category, actor = '') {
  const needle = actor.trim().toLowerCase();
  return (rows ?? []).filter(
    (r) =>
      (!category || r.category === category) && (!needle || r.actor.toLowerCase().includes(needle)),
  );
}

export function dailyCard(raw) {
  const r = raw ?? {};
  const recording = r.recording ?? r.Recording ?? [];
  const events = r.events ?? r.Events ?? [];
  const hours = recording.reduce((s, x) => s + Number(x.hours ?? x.Hours ?? 0), 0);
  const bytes = recording.reduce((s, x) => s + Number(x.bytes ?? x.Bytes ?? 0), 0);
  return {
    hours: Math.round(hours * 100) / 100,
    bytes,
    gb: Number((bytes / 1073741824).toFixed(2)),
    disconnects: Number(r.disconnects ?? r.Disconnects ?? 0),
    events: events
      .map((e) => ({ type: e.eventType ?? e.EventType ?? '', count: Number(e.count ?? e.Count ?? 0) }))
      .sort((a, b) => b.count - a.count),
  };
}

export function patrolLabel(p) {
  const r = p ?? {};
  const steps = r.steps ?? []; 
  return `${r.name ?? '未命名'} · 頻道${r.channelId ?? '-'} · ${r.windowStart ?? '00:00'}-${r.windowEnd ?? '23:59'} · ${steps.length} 步`;
}

export function scheduleLabel(s, { short = false } = {}) {
  const r = s ?? {};
  const mask = Number(r.daysMask ?? r.DaysMask ?? 0);
  const dayNames = ['日', '一', '二', '三', '四', '五', '六'];
  const runs = [];
  let runStart = -1;
  for (let d = 0; d <= 7; d++) {
    const on = d < 7 && (mask & (1 << d)) !== 0;
    if (on && runStart < 0) runStart = d;
    if (!on && runStart >= 0) {
      runs.push([runStart, d - 1]);
      runStart = -1;
    }
  }

  const fmt = (m) => `${String(Math.floor(m / 60)).padStart(2, '0')}:${String(m % 60).padStart(2, '0')}`;
  const days = runs.length
    ? runs
        .map(([a, b]) => (a === b ? (short ? `週${dayNames[a]}` : `週${a + 1}`) : `${dayNames[a]}-${dayNames[b]}`))
        .join('、')
    : '無';
  return `${days} ${fmt(Number(r.startMinute ?? r.StartMinute ?? 0))}-${fmt(Number(r.endMinute ?? r.EndMinute ?? 0))}`;
}

export function scheduleInEffect(s, date = new Date()) {
  const r = s ?? {};
  const mask = Number(r.daysMask ?? r.DaysMask ?? 0);
  const local = new Date(date.getFullYear(), date.getMonth(), date.getDate());
  const day = local.getDay(); // 0=日 … 6=六
  const minute = date.getHours() * 60 + date.getMinutes();
  const start = Number(r.startMinute ?? r.StartMinute ?? 0);
  const end = Number(r.endMinute ?? r.EndMinute ?? 0);
  return r.enabled === false ? false : (mask & (1 << day)) !== 0 && minute >= start && minute < end;
}

export function focusLayout(cells, cols = 4) {
  const list = (cells ?? []).map((cell, index) => ({ cell, index }));
  if (list.length === 0) return [];
  const c = Math.max(1, cols);
  const base = 1 / c;
  const out = new Array(list.length);

  const criticals = list.filter((x) => (x.cell.priority ?? x.cell.Priority) === 'critical');
  const normals = list.filter((x) => (x.cell.priority ?? x.cell.Priority) !== 'critical');

  const perRow = Math.max(1, Math.floor(c / 2));
  const critRows = Math.max(1, Math.ceil(criticals.length / perRow));
  criticals.forEach((x, i) => {
    const col = (i % perRow) * 2;
    const row = Math.floor(i / perRow);
    out[x.index] = { left: col * base, top: row * (2 * base), width: 2 * base, height: 2 * base };
  });

  const normalTop = critRows * (2 * base);
  const normalRows = Math.max(1, Math.ceil(normals.length / c));
  normals.forEach((x, i) => {
    const col = i % c;
    const row = Math.floor(i / c);
    out[x.index] = { left: col * base, top: normalTop + row * base, width: base, height: base };
  });

  return out;
}