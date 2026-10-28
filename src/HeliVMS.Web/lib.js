import { t } from './i18n.js';

/**
 * 可直接放進 HTML 的譯文（M243）。
 * t() 的輸出**不能**直接插進 innerHTML：它會把 {params} 原樣內插，
 * 而參數可能來自伺服器回應（例如授權缺項清單），等同把未跳脫資料塞進 DOM。
 * tx() 負責跳脫，讓「譯文」與「資料」在安全規則裡維持同一個標準。
 */
export function tx(key, params) {
  return esc(t(key, params));
}

export const PRIORITY_RANK = { critical: 4, high: 3, normal: 2, low: 1 };

const HTML_ESCAPES = { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' };

/** Escape untrusted text for interpolation into an innerHTML template (text and attribute context). */
export function esc(value) {
  if (value === null || value === undefined) return '';
  return String(value).replace(/[&<>"']/g, (c) => HTML_ESCAPES[c]);
}

/** Escape for double-quoted attribute values, keeping decimals and negative signs of chart geometry. */
export function escPct(value) {
  if (!Number.isFinite(value)) return '0';
  return esc(String(value));
}

/** Build the API authorization header from the stored key; empty key means no header at all. */
export function bearerHeader(apiKey) {
  const k = (apiKey ?? '').trim();
  return k ? { Authorization: `Bearer ${k}` } : {};
}

export function formatTimestamp(iso) {
  if (!iso) return '';
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return '';
  return d.toISOString().slice(11, 19);
}

export function eventRow(e) {
  return {
    id: e.sourceId ?? e.SourceId,
    time: formatTimestamp(e.occurredAtUtc ?? e.OccurredAtUtc),
    source: Number(e.source ?? e.Source ?? 0),
    text: e.text ?? e.Text ?? '',
    status: e.status ?? e.Status,
  };
}

export function filterBoardRows(rows, filter) {
  const list = Array.isArray(rows) ? rows : [];
  if (!filter) return list;
  return filter === 'overdue'
    ? list.filter((r) => r.overdue)
    : list.filter((r) => r.status === filter);
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

/** HLS VOD 播放清單網址（§14.3 串流）：遠程回放的單一入口。 */
export function buildPlaylistQuery({ channelId, stream, from, to }) {
  const params = new URLSearchParams({
    stream,
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
  });
  return `/api/stream/${channelId}/playlist.m3u8?${params.toString()}`;
}

/**
 * 跨機回放的 HLS VOD 播放清單網址（§14.1 #10，M301）。
 * 上層 NVR 代為向子節點取片，瀏覽器只跟上層說話（子節點金鑰不出現在前端）。
 * 回傳清單中的片段網址已被伺服器改寫為同一代理前綴，故 attachPlaylist 不必改。
 */
export function buildCentralPlaylistQuery({ nodeId, channelId, stream, from, to }) {
  const params = new URLSearchParams({
    channelId: String(channelId),
    stream,
    from: new Date(from).toISOString(),
    to: new Date(to).toISOString(),
  });
  return `/api/central/playback/${nodeId}/playlist.m3u8?${params.toString()}`;
}

/** MSE 播放 fMP4 需要的 MIME（依瀏覽器支援度挑，Safari 不走這條路）。 */
export function mseMimeType(canPlayMp4 = false, canPlayAvc1 = false) {
  if (canPlayMp4) return 'video/mp4; codecs="avc1.64001f,mp4a.40.2"';
  if (canPlayAvc1) return 'video/mp4; codecs="avc1.42E01E"';
  return 'video/mp4';
}

/**
 * 解析伺服器產生的 HLS VOD 清單（§14.3 串流）。
 * 只認得本系統會寫出的標籤；EXT-X-MAP 與 EXTINF 之外的一律忽略。
 */
export function parseM3u8(text) {
  const lines = String(text ?? '')
    .split(/\r?\n/)
    .map((line) => line.trim())
    .filter(Boolean);

  if (!lines.includes('#EXTM3U')) return null;

  const playlist = { initUri: null, targetDuration: 0, duration: 0, segments: [] };
  let pendingDuration = null;
  let pendingStart = null;

  for (const line of lines) {
    if (line.startsWith('#EXT-X-MAP:')) {
      const uri = line.match(/URI="([^"]+)"/);
      if (uri) playlist.initUri = uri[1];
      continue;
    }
    if (line.startsWith('#EXT-X-TARGETDURATION:')) {
      const value = Number.parseInt(line.slice('#EXT-X-TARGETDURATION:'.length), 10);
      if (Number.isFinite(value)) playlist.targetDuration = value;
      continue;
    }
    if (line.startsWith('#EXT-X-PROGRAM-DATE-TIME:')) {
      const parsed = Date.parse(line.slice('#EXT-X-PROGRAM-DATE-TIME:'.length));
      pendingStart = Number.isFinite(parsed) ? parsed : null;
      continue;
    }
    if (line.startsWith('#EXTINF:')) {
      const value = Number.parseFloat(line.slice('#EXTINF:'.length));
      pendingDuration = Number.isFinite(value) && value > 0 ? value : 0;
      continue;
    }
    if (line.startsWith('#')) continue;
    playlist.segments.push({ uri: line, duration: pendingDuration ?? 0, start: pendingStart });
    playlist.duration += pendingDuration ?? 0;
    pendingDuration = null;
    pendingStart = null;
  }

  return playlist;
}

/** 播放清單的時段秒數上限；伺服端 24 小時上限的對應值，用於介面提示。 */
export const PLAYLIST_WINDOW_LIMIT_S = 24 * 60 * 60;

function dataViewOf(bytes) {
  if (!bytes) return null;
  const buffer = bytes instanceof ArrayBuffer ? bytes : bytes.buffer;
  const offset = bytes instanceof ArrayBuffer ? 0 : (bytes.byteOffset ?? 0);
  const length = bytes instanceof ArrayBuffer ? bytes.byteLength : (bytes.byteLength ?? bytes.length);
  if (!buffer || !length || length < 8) return null;
  return new DataView(buffer, offset, length);
}

/** 24 位盒子旗標（DataView 沒有 getUintBE，那是 Buffer 的 API）。 */
function readUint24(view, offset) {
  return (view.getUint8(offset) << 16) | (view.getUint8(offset + 1) << 8) | view.getUint8(offset + 2);
}

/**
 * fMP4 盤子游標：size(4)+type(4)，size==1 是 64 位長度、size==0 延伸到結尾。
 * 只走盤子層級並依 size 跳過 mdat 載荷，所以十萬位元組的段檔也只讀到幾百個位元組。
 */
function mp4Boxes(view, start, end) {
  const boxes = [];
  let offset = start;
  while (offset + 8 <= end) {
    let size = view.getUint32(offset);
    const type = String.fromCharCode(
      view.getUint8(offset + 4),
      view.getUint8(offset + 5),
      view.getUint8(offset + 6),
      view.getUint8(offset + 7),
    );
    let header = 8;
    if (size === 1) {
      if (offset + 16 > end) break;
      size = Number(view.getBigUint64(offset + 8));
      header = 16;
    } else if (size === 0) {
      size = end - offset;
    }
    if (size < header || offset + size > end) break;
    boxes.push({ type, body: offset + header, end: offset + size });
    offset += size;
  }
  return boxes;
}

/**
 * 由初始化段（EXT-X-MAP 的 init.mp4）解析每軌的 track_id／timescale／handler。
 * 影片軌的時間戳刻度是它自己的 timescale（實機是 90000），沒有這份換算表就沒辦法
 * 把 tfdt／trun 換成秒，也就沒辦法把各段接上同一條時間軸。
 */
export function parseFmp4InitTracks(bytes) {
  const view = dataViewOf(bytes);
  if (!view) return null;
  try {
    const moov = mp4Boxes(view, 0, view.byteLength).find((box) => box.type === 'moov');
    if (!moov) return null;
    const tracks = [];
    for (const trak of mp4Boxes(view, moov.body, moov.end)) {
      if (trak.type !== 'trak') continue;
      let id = null;
      let timescale = null;
      let handler = null;
      for (const box of mp4Boxes(view, trak.body, trak.end)) {
        if (box.type === 'tkhd') id = view.getUint32(box.body + (view.getUint8(box.body) === 1 ? 20 : 12));
        if (box.type !== 'mdia') continue;
        for (const inner of mp4Boxes(view, box.body, box.end)) {
          if (inner.type === 'mdhd') timescale = view.getUint32(inner.body + (view.getUint8(inner.body) === 1 ? 20 : 12));
          if (inner.type === 'hdlr' && inner.body + 12 <= inner.end) {
            handler = String.fromCharCode(
              view.getUint8(inner.body + 8),
              view.getUint8(inner.body + 9),
              view.getUint8(inner.body + 10),
              view.getUint8(inner.body + 11),
            );
          }
        }
      }
      if (id && timescale) tracks.push({ id, timescale, handler });
    }
    return tracks.length > 0 ? tracks : null;
  } catch {
    return null;
  }
}

/**
 * 影片軌在這一個媒體段裡的時間跨度（秒，起點是該段自己的零點）。
 *
 * 每個錄影檔是獨立的 ffmpeg 行程寫出的，時間戳都從 0 重新開始，所以瀏覽器不會
 * 自動把後段接在前段後面——不設 timestampOffset 直接 append，後段的幀會覆蓋到
 * 前段的時間軸上，實際能播的只剩第一段。接縫的依據只能取真實媒體長度：m3u8 的
 * EXTINF 是伺服器量的牆鐘（含 ffmpeg 啟動與收尾，實機每段多約一到兩秒），
 * 用它累加會讓時間軸長出成串空隙、時鐘也逐段漂移。取樣解析不動 mdat，成本與
 * 盒子游標同級。B-frame 的 composition offset 只取第一個樣本近似（本系統錄的是
 * 攝影機的基準線 H.264，沒有 B-frame）。
 */
export function parseFmp4VideoSpan(tracks, bytes) {
  const video = (Array.isArray(tracks) ? tracks : []).find((track) => track.handler === 'vide');
  const view = dataViewOf(bytes);
  if (!video || !view) return null;
  try {
    let start = null;
    let end = null;
    for (const moof of mp4Boxes(view, 0, view.byteLength)) {
      if (moof.type !== 'moof') continue;
      for (const traf of mp4Boxes(view, moof.body, moof.end)) {
        if (traf.type !== 'traf') continue;
        let trackId = null;
        let base = null;
        let defaultDuration = null;
        let duration = 0;
        let composition = 0;
        for (const box of mp4Boxes(view, traf.body, traf.end)) {
          if (box.type === 'tfhd') {
            const flags = readUint24(view, box.body + 1);
            trackId = view.getUint32(box.body + 4);
            let cursor = box.body + 8;
            if (flags & 0x000001) cursor += 8;
            if (flags & 0x000002) cursor += 4;
            if (flags & 0x000008) {
              defaultDuration = view.getUint32(cursor);
              cursor += 4;
            }
          } else if (box.type === 'tfdt') {
            base = view.getUint8(box.body) === 1
              ? Number(view.getBigUint64(box.body + 4))
              : view.getUint32(box.body + 4);
          } else if (box.type === 'trun') {
            const flags = readUint24(view, box.body + 1);
            const signed = view.getUint8(box.body) === 1;
            const count = view.getUint32(box.body + 4);
            let cursor = box.body + 8;
            if (flags & 0x000001) cursor += 4;
            if (flags & 0x000004) cursor += 4;
            for (let i = 0; i < count && cursor + 4 <= box.end; i++) {
              let sampleDuration = defaultDuration ?? 0;
              if (flags & 0x000100) {
                sampleDuration = view.getUint32(cursor);
                cursor += 4;
              }
              if (flags & 0x000200) cursor += 4;
              if (flags & 0x000400) cursor += 4;
              if (flags & 0x000800) {
                if (i === 0) composition = signed ? view.getInt32(cursor) : view.getUint32(cursor);
                cursor += 4;
              }
              duration += sampleDuration;
            }
          }
        }
        if (trackId !== video.id || base === null) continue;
        const first = base + composition;
        const last = base + duration + composition;
        if (start === null || first < start) start = first;
        if (end === null || last > end) end = last;
      }
    }
    if (start === null || end === null || end <= start) return null;
    return { start: start / video.timescale, end: end / video.timescale };
  } catch {
    return null;
  }
}

/**
 * 媒體時間軸 → 錄影牆鐘（毫秒）。
 *
 * 每段的對應是「該段錄影起點＋段內比例插值」。牆鐘長度（EXTINF，伺服器量的端到端
 * 時間）比媒體長度多一個連線暖機量級的常數，若用單純的「起點＋經過秒數」，時鐘會
 * 在每段尾端落後一兩秒、又在段界跳回去；比例插值讓段界連續、最大誤差減半。範圍
 * 外夾到最近的端點，游標停在邊界時不會顯示空白。
 */
export function buildRecordingClock(timeline) {
  const entries = (Array.isArray(timeline) ? timeline : [])
    .filter(
      (entry) =>
        Number.isFinite(entry?.wallStartMs) &&
        Number.isFinite(entry?.mediaStart) &&
        Number.isFinite(entry?.mediaEnd) &&
        entry.mediaEnd > entry.mediaStart,
    )
    .sort((a, b) => a.mediaStart - b.mediaStart);
  return {
    at(seconds) {
      if (entries.length === 0 || !Number.isFinite(seconds)) return null;
      let lo = 0;
      let hi = entries.length - 1;
      while (lo < hi) {
        const mid = (lo + hi + 1) >> 1;
        if (entries[mid].mediaStart <= seconds) lo = mid;
        else hi = mid - 1;
      }
      const entry = entries[lo];
      const span = entry.mediaEnd - entry.mediaStart;
      const wall = Number.isFinite(entry.wallDurS) && entry.wallDurS > 0 ? entry.wallDurS : span;
      const inside = Math.min(Math.max(seconds, entry.mediaStart), entry.mediaEnd);
      return entry.wallStartMs + ((inside - entry.mediaStart) / span) * wall * 1000;
    },
  };
}

/**
 * 回放時鐘的顯示格式（本地時間）。
 * 與 formatTimestamp（UTC，事件表慣例）刻意不同：回放時段欄位是 datetime-local、
 * 桌面端回放也是顯示本地時間，時鐘跟著它們走，操作員才不用自己換算時差。
 */
export function formatLocalClock(ms) {
  if (!Number.isFinite(ms)) return '';
  const d = new Date(ms);
  const pad = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`;
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
  if (gapFraction >= 0.999) return t('gap.noRecording');
  if (gapFraction <= 0.001) return t('gap.fullDay');
  return t('gap.missing', { pct: (gapFraction * 100).toFixed(0) });
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

export function pollGate(activeTagName, docHidden) {
  if (docHidden) return false;
  if (typeof activeTagName === 'string' && /^(INPUT|SELECT|TEXTAREA|BUTTON)$/.test(activeTagName.toUpperCase())) {
    return false;
  }
  return true;
}

export function parseLogin(payload) {
  if (!payload || typeof payload !== 'object') return { ok: false, error: t('state.badResponse') };
  if (typeof payload.role === 'string' && payload.role.length > 0) {
    return { ok: true, role: payload.role, displayName: payload.displayName ?? null, token: payload.token ?? null };
  }
  return { ok: false, error: payload.error || t('state.loginFailed') };
}

export function accountRows(list) {
  return (list ?? [])
    .map((a) => ({
      id: Number(a.id ?? a.Id ?? 0),
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
    recordingRetentionDays: Number(r.recordingRetentionDays ?? r.RecordingRetentionDays ?? 30),
    recordingWatermarkGb: Number(r.recordingWatermarkGb ?? r.RecordingWatermarkGb ?? 0),
    alarmRetentionDays: Number(r.alarmRetentionDays ?? r.AlarmRetentionDays ?? 365),
    auditRetentionDays: Number(r.auditRetentionDays ?? r.AuditRetentionDays ?? 365),
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
  return t('plan.summary', {
    name: r.name ?? t('common.unnamed'),
    channel: r.channelId ?? '-',
    start: r.windowStart ?? '00:00',
    end: r.windowEnd ?? '23:59',
    steps: steps.length,
  });
}

export function evRows(rows) {
  return (rows ?? []).map((m) => ({
    id: m.id ?? m.Id ?? 0,
    status: m.status ?? m.Status ?? '?',
    createdAt: m.createdAt ?? m.CreatedAt ?? '',
    items: Number(m.items ?? m.Items ?? 0),
  }));
}

export function backupRows(rows) {
  return (rows ?? []).map((r, i) => ({
    seq: i + 1,
    runAt: r.runAt ?? r.RunAt ?? '',
    copied: Number(r.copiedCount ?? r.CopiedCount ?? 0),
    bytes: Number(r.copiedBytes ?? r.CopiedBytes ?? 0),
    failed: Number(r.failedCount ?? r.FailedCount ?? 0),
    advanced: !!(r.checkpointUtc ?? r.CheckpointUtc),
    target: r.targetRoot ?? r.TargetRoot ?? '',
  }));
}

export function centralRows(nodes) {
  return (Array.isArray(nodes) ? nodes : []).map((n) => ({
    id: n.id,
    name: n.name ?? '',
    baseUrl: n.baseUrl ?? '',
    enabled: n.enabled !== false,
    nodeId: n.nodeId ?? '',
    version: n.version ?? '',
    channels: Number(n.channels ?? 0),
    online: Boolean(n.online),
    lastSeenUtc: n.lastSeenUtc ?? '',
    lastError: n.lastError ?? '',
  }));
}

export function centralChannelRows(channels) {
  return (Array.isArray(channels) ? channels : []).map((c) => ({
    nodeId: Number(c.nodeId ?? 0),
    nodeName: c.nodeName ?? '',
    channelId: Number(c.channelId ?? 0),
    channelName: c.channelName ?? '',
    enabled: c.enabled !== false,
  }));
}

export function centralAlarmRows(rows) {
  return (rows ?? []).map((a) => ({
    nodeId: Number(a.nodeId ?? a.NodeId ?? 0),
    nodeName: a.nodeName ?? a.NodeName ?? '',
    eventId: Number(a.eventId ?? a.EventId ?? 0),
    channelId: Number(a.channelId ?? a.ChannelId ?? 0),
    eventType: a.eventType ?? a.EventType ?? '',
    start: formatTimestamp(a.startUtc ?? a.StartUtc ?? ''),
    status: a.status ?? a.Status ?? '',
    priority: a.priority ?? a.Priority ?? '',
    due: a.dueUtc ?? a.DueUtc ?? null,
    owner: a.owner ?? a.Owner ?? '',
    assignedTo: a.assignedTo ?? a.AssignedTo ?? '',
    overdue: !!(a.overdue ?? a.Overdue),
  }));
}

export function doorRows(rows) {
  return (rows ?? []).map((e) => ({
    time: formatTimestamp(e.occurredAtUtc ?? e.OccurredAtUtc ?? ''),
    device: Number(e.deviceId ?? e.DeviceId ?? 0),
    door: Number(e.doorId ?? e.DoorId ?? 0),
    card: e.cardId ?? e.CardId ?? '',
    direction: e.direction ?? e.Direction ?? '',
    granted: !!(e.granted ?? e.Granted),
    reason: e.reason ?? e.Reason ?? '',
  }));
}

export function detRows(rows) {
  return (rows ?? []).map((d) => ({
    time: formatTimestamp(d.detectedUtc ?? d.DetectedUtc ?? ''),
    channel: Number(d.channelId ?? d.ChannelId ?? 0),
    cls: d.class ?? d.Class ?? '',
    conf: Number(d.confidence ?? d.Confidence ?? 0),
    x: Number(d.x ?? d.X ?? 0),
    y: Number(d.y ?? d.Y ?? 0),
    box: `${Number(d.w ?? d.W ?? 0)}×${Number(d.h ?? d.H ?? 0)}`,
  }));
}

export function notifRows(rows) {
  return (rows ?? []).map((n) => ({
    time: formatTimestamp(n.tsUtc ?? n.TsUtc ?? ''),
    channel: Number(n.channelId ?? n.ChannelId ?? 0),
    event: n.eventType ?? n.EventType ?? '',
    route: n.route ?? n.Route ?? '',
    ok: !!(n.ok ?? n.Ok),
    attempts: Number(n.attempts ?? n.Attempts ?? 0),
    detail: n.detail ?? n.Detail ?? '',
  }));
}

export function exportRows(rows) {
  return (rows ?? []).map((j) => ({
    id: Number(j.id ?? j.Id ?? 0),
    channel: Number(j.channelId ?? j.ChannelId ?? 0),
    stream: j.stream ?? j.Stream ?? '',
    start: formatTimestamp(j.startUtc ?? j.StartUtc ?? ''),
    end: formatTimestamp(j.endUtc ?? j.EndUtc ?? ''),
    status: j.status ?? j.Status ?? '',
    file: j.fileSizeBytes ?? j.FileSizeBytes ?? null,
    sha: (j.sha256 ?? j.Sha256 ?? '').slice(0, 12),
    error: j.error ?? j.Error ?? '',
  }));
}

export function providerRows(rows) {
  return (rows ?? []).map((p) => ({
    id: Number(p.id ?? p.Id ?? 0),
    name: p.name ?? p.Name ?? '',
    kind: p.kind ?? p.Kind ?? '',
    enabled: !!(p.enabled ?? p.Enabled),
    config: p.configJson ?? p.ConfigJson ?? '',
    createdAt: p.createdAt ?? p.CreatedAt ?? '',
  }));
}

export function holdRows(rows) {
  return (rows ?? []).map((h) => ({
    id: Number(h.id ?? h.Id ?? 0),
    channel: Number(h.channelId ?? h.ChannelId ?? 0),
    from: formatTimestamp(h.fromUtc ?? h.FromUtc ?? ''),
    to: formatTimestamp(h.toUtc ?? h.ToUtc ?? ''),
    reason: h.reason ?? h.Reason ?? '',
    by: h.createdBy ?? h.CreatedBy ?? '',
    active: !!(h.active ?? h.Active),
    revokedBy: h.revokedBy ?? h.RevokedBy ?? '',
  }));
}

export function ruleRows(rows) {
  return (rows ?? []).map((r) => ({
    id: Number(r.id ?? r.Id ?? 0),
    name: r.name ?? r.Name ?? '',
    event: r.eventType ?? r.EventType ?? '',
    channel: r.channelId ?? r.ChannelId ?? 0,
    keyword: r.keyword ?? r.Keyword ?? '',
    channels: r.channels ?? r.Channels ?? '',
    enabled: !!(r.enabled ?? r.Enabled),
    min: Number(r.minEventsInWindow ?? r.MinEventsInWindow ?? 1),
  }));
}

export function shareRows(rows) {
  return (rows ?? []).map((s) => ({
    id: Number(s.id ?? s.Id ?? 0),
    kind: s.kind ?? s.Kind ?? '',
    path: s.resourcePath ?? s.ResourcePath ?? '',
    label: s.label ?? s.Label ?? '',
    token: (s.token ?? s.Token ?? '').slice(0, 12),
    createdBy: s.createdBy ?? s.CreatedBy ?? '',
    uses: `${s.useCount ?? s.UseCount ?? 0}/${s.maxUses ?? s.MaxUses ?? 0}`,
    active: !!(s.active ?? s.Active),
  }));
}

export function redRows(rows) {
  return (rows ?? []).map((r) => ({
    id: Number(r.id ?? r.Id ?? 0),
    source: r.sourceType ?? r.SourceType ?? '',
    ref: Number(r.refId ?? r.RefId ?? 0),
    channel: Number(r.channelId ?? r.ChannelId ?? 0),
    time: formatTimestamp(r.occurredAtUtc ?? r.OccurredAtUtc ?? ''),
    rect: `${r.x ?? r.X ?? 0},${r.y ?? r.Y ?? 0} ${r.width ?? r.Width ?? 0}×${r.height ?? r.Height ?? 0}`,
    filled: !!(r.filled ?? r.Filled),
  }));
}

export function repRows(rows) {
  return (rows ?? []).map((j) => ({
    id: Number(j.id ?? j.Id ?? 0),
    src: j.sourcePath ?? j.SourcePath ?? '',
    dst: j.destinationPath ?? j.DestinationPath ?? '',
    minutes: Number(j.intervalMinutes ?? j.IntervalMinutes ?? 0),
    enabled: !!(j.enabled ?? j.Enabled),
    lastResult: j.lastResult ?? j.LastResult ?? '',
    lastError: j.lastError ?? j.LastError ?? '',
    fails: Number(j.consecutiveFailures ?? j.ConsecutiveFailures ?? 0),
    due: !!(j.due ?? j.Due),
  }));
}

export function alarmRows(rows) {
  return (rows ?? []).map((b) => ({
    id: Number(b.eventId ?? b.EventId ?? 0),
    channel: Number(b.channelId ?? b.ChannelId ?? 0),
    event: b.eventType ?? b.EventType ?? '',
    start: formatTimestamp(b.startUtc ?? b.StartUtc ?? ''),
    status: b.status ?? b.Status ?? '',
    priority: b.priority ?? b.Priority ?? '',
    due: b.dueUtc ?? b.DueUtc ?? null,
    owner: b.owner ?? b.Owner ?? '',
    overdue: !!(b.overdue ?? b.Overdue),
  }));
}

export function sysMetricsRows(metrics) {
  const m = metrics ?? {};
  return {
    captured: formatTimestamp(m.capturedAtUtc ?? m.CapturedAtUtc ?? ''),
    uptimeMinutes: Number(m.uptimeMinutes ?? m.UptimeMinutes ?? 0),
    workingSetMb: Number(m.workingSetMb ?? m.WorkingSetMb ?? 0),
    managedHeapMb: Number(m.managedHeapMb ?? m.ManagedHeapMb ?? 0),
    cpuPercent: Number(m.cpuPercent ?? m.CpuPercent ?? 0),
    disks: (m.disks ?? m.Disks ?? []).map((d) => ({
      name: d.name ?? d.Name ?? '',
      totalMb: Number(d.totalMb ?? d.TotalMb ?? 0),
      freeMb: Number(d.freeMb ?? d.FreeMb ?? 0),
      format: d.format ?? d.Format ?? '',
    })),
  };
}

export function sysTrendRows(points) {
  const list = (points ?? []).map((p) => ({
    captured: formatTimestamp(p.capturedAtUtc ?? p.CapturedAtUtc ?? ''),
    workingSetGb: Number(p.workingSetGb ?? p.WorkingSetGb ?? 0),
  }));
  return list.sort((a, b) => (a.captured < b.captured ? -1 : a.captured > b.captured ? 1 : 0));
}

export function sysDiskTrendRows(points) {
  const list = (points ?? []).map((p) => ({
    captured: formatTimestamp(p.capturedAtUtc ?? p.CapturedAtUtc ?? ''),
    name: p.name ?? p.Name ?? '',
    freeMb: Number(p.freeMb ?? p.FreeMb ?? 0),
    totalMb: Number(p.totalMb ?? p.TotalMb ?? 0),
  }));
  return list.sort((a, b) => (a.captured < b.captured ? -1 : a.captured > b.captured ? 1 : 0));
}

export function clipViewRows(hits) {
  return (hits ?? [])
    .map((h) => ({
      refId: Number(h.refId ?? h.RefId ?? 0),
      sourceType: h.sourceType ?? h.SourceType ?? '',
      label: h.label ?? h.Label ?? '',
      score: Number(h.score ?? h.Score ?? 0),
    }))
    .sort((a, b) => b.score - a.score);
}

export function audioRows(items) {
  return (items ?? [])
    .map((a) => ({
      eventId: Number(a.eventId ?? a.EventId ?? 0),
      channelId: Number(a.channelId ?? a.ChannelId ?? 0),
      startedAtUtc: a.startedAtUtc ?? a.StartedAtUtc ?? '',
      durationMs: Number(a.durationMs ?? a.DurationMs ?? 0),
      mime: a.mime ?? a.Mime ?? '',
      sha256: a.sha256 ?? a.Sha256 ?? '',
    }))
    .sort((a, b) => a.eventId - b.eventId);
}

export function fuseViewRows(results) {
  return (results ?? [])
    .map((r) => ({
      key: r.key ?? r.Key ?? '',
      score: Number(r.score ?? r.Score ?? 0),
    }))
    .sort((a, b) => b.score - a.score);
}

export function smartwallLayout(cells, opts = {}) {
  const cols = Math.max(1, Number(opts.cols) || 4);
  const rows = Math.max(1, Number(opts.rows) || 4);
  const capacity = cols * rows;
  const ordered = (cells ?? [])
    .map((c) => ({
      channelId: Number(c.channelId ?? c.ChannelId ?? 0),
      eventType: c.eventType ?? c.EventType ?? '',
      priority: c.priority ?? c.Priority ?? '',
      rank: Number(c.rank ?? c.Rank ?? 0),
      highlight: Boolean(c.highlight ?? c.Highlight ?? false),
    }))
    .sort((a, b) => a.rank - b.rank)
    .slice(0, capacity);
  return ordered.map((c, i) => ({
    ...c,
    col: (i % cols) + 1,
    row: Math.floor(i / cols) + 1,
    colSpan: 1,
  }));
}

export function scheduleLabel(s, { short = false } = {}) {
  const r = s ?? {};
  const mask = Number(r.daysMask ?? r.DaysMask ?? 0);
  const dayNames = [0, 1, 2, 3, 4, 5, 6].map((d) => t(`day.${d}`));
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
        .map(([a, b]) =>
          a === b
            ? short
              ? t('week.day', { day: dayNames[a] })
              : t('week.number', { n: a + 1 })
            : `${dayNames[a]}-${dayNames[b]}`,
        )
        .join(t('list.separator'))
    : t('value.none');
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