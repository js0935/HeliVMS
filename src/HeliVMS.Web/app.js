import { applyDom, DEFAULT_LOCALE, locale, setLocale, t } from './i18n.js';
import { createLiveViewer } from './live.js';
import {
  accountRows,
  ackRate,
  auditRows,
  auditFilter,
  buildTimelineQuery,
  buildPlaylistQuery,
  bearerHeader,
  canAct,
  configCard,
  dailyCard,
  esc,
  escPct,
  eventRow,
  filterBoardRows,
  focusLayout,
  formatTimestamp,
  mseMimeType,
  parseM3u8,
  evRows,
  backupRows,
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
  patrolLabel,
  scheduleInEffect,
  scheduleLabel,
  tx,
  gapLabel,
  gridLayout,
  parseLogin,
  parseWsMessage,
  pinStyles,
  posTotals,
  pollGate,
  smartwallSnapshot,
  tileClass,
  tileLabel,
} from './lib.js';

const $ = (id) => document.getElementById(id);
const key = () => (localStorage.getItem('helivms.apiKey') || $('apikey')?.value || '').trim();
const POLL_MS = 12000; // REST 面板輪詢（門禁/偵測/通知/匯出/排程…）——分頁隱藏或焦點在輸入框時暫停
let boardFilter = ''; // 警報看板過濾：'' | pending | acknowledged | actioned | false_alarm | overdue

/** Single authenticated transport: every API call must go through here so the bearer key is always sent. */
async function apiRaw(path, init = {}) {
  const headers = new Headers(init.headers);
  for (const [name, value] of Object.entries(bearerHeader(key()))) {
    headers.set(name, value);
  }
  try {
    const response = await fetch(path, { ...init, headers });
    setConn(true);
    return response;
  } catch (err) {
    setConn(false);
    throw err;
  }
}

/** 連線旗標：任何 fetch 成功即可正常;網路層失敗時在頂列顯示「連線中斷」。（HTTP 錯誤狀態不算斷線） */
function setConn(ok) {
  const el = $('conn');
  if (!el) return;
  el.textContent = ok ? '' : t('state.disconnected');
  el.classList.toggle('off', !ok);
}

async function api(path, init = {}) {
  const response = await apiRaw(path, init);
  if (!response.ok) throw new Error(`${path} -> ${response.status}`);
  return response.json();
}

/**
 * 把一次授權閘門的回應寫進面板的 live region，回傳是否成功。
 * 閘門是整條路徑一起擋的，所以同一面板的每個動作都可能拿到 403 與同一句 error；
 * 動作層若不顯示出來，使用者只會看到畫面原封不動地回來，像是按了沒反應。
 */
async function feedback(msgId, resp, okText = () => '') {
  if (!resp) {
    $(msgId).textContent = t('state.disconnected');
    return false;
  }
  const info = await resp.json().catch(() => null);
  $(msgId).textContent = resp.ok ? okText(info) : info?.error ?? t('state.failed');
  return resp.ok;
}

/**
 * 受授權閘門保護的讀取。回 null 代表「被擋下或連不上」，呼叫端要清空表格而不是畫成 0 筆——
 * 空表與沒授權是兩件事，混在一起會讓人以為這個頻道真的沒有資料。
 */
async function gatedFetch(msgId, url) {
  const resp = await apiRaw(url).catch(() => null);
  if (!resp || !resp.ok) {
    await feedback(msgId, resp);
    return null;
  }
  $(msgId).textContent = '';
  return resp.json().catch(() => []);
}

async function refreshHealth() {
  try {
    const h = await api('/api/health');
    $('health').textContent = `ok · ${h.channels} ch · db ${h.database}`;
    $('health').classList.add('on');
  } catch (err) {
    $('health').textContent = String(err);
  }
}

async function refreshChannels() {
  const channels = await api('/api/channels');
  $('channels').innerHTML = channels
    .map((c) => `<li>${esc(c.id)} · ${esc(c.name ?? c.location ?? '')}</li>`)
    .join('');
}

function readSession() {
  try {
    return JSON.parse(sessionStorage.getItem('helivms.session') || 'null');
  } catch {
    return null;
  }
}

function storeSession(session) {
  if (session) {
    sessionStorage.setItem('helivms.session', JSON.stringify(session));
  } else {
    sessionStorage.removeItem('helivms.session');
  }
  updateChrome();
}

async function submitLogin(event) {
  event.preventDefault();
  const username = $('user').value.trim();
  const password = $('pass').value;
  $('pass').value = '';
  let payload = {};
  try {
    const res = await apiRaw('/api/accounts/authenticate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ username, password }),
    });
    payload = await res.json().catch(() => ({}));
  } catch {
    payload = {};
  }
  const state = parseLogin(payload);
  $('login-msg').textContent = state.ok ? '' : state.error;
  if (state.ok) {
    storeSession({ role: state.role, name: state.displayName });
    if (canAct(state.role)) {
      await Promise.allSettled([renderAccounts(), renderConfig(), renderAudit(), renderEvidence(), renderBackup(), renderProviders(), renderHolds(), renderRules(), renderShares(), renderReds(), renderRep(), renderHealth()]);
    }
  }
  await renderBoard();
}

function logout() {
  storeSession(null);
  window.location.reload();
}

function updateChrome() {
  const session = readSession();
  const who = $('who');
  who.textContent = session ? `${session.role} · ${session.name ?? ''}`.trim() : t('state.notSignedIn');
  who.classList.toggle('on', Boolean(session));
  const admin = canAct(session?.role);
  $('board').classList.toggle('gated', !admin);
  $('accounts-panel').hidden = !admin;
  $('config-panel').hidden = !admin;
  $('audit-panel').hidden = !admin;
  $('evidence-panel').hidden = !admin;
  $('backup-panel').hidden = !admin;
  $('provider-panel').hidden = !admin;
  $('hold-panel').hidden = !admin;
  $('rule-panel').hidden = !admin;
  $('share-panel').hidden = !admin;
  $('red-panel').hidden = !admin;
  $('rep-panel').hidden = !admin;
  $('health-panel').hidden = !admin;
  $('admin-nav').hidden = !admin;
  $('logout').hidden = !session;
}

let csvUrl = '';

async function renderAudit() {
  const all = auditRows((await api('/api/audit?limit=200')).items);
  const category = $('audit-category').value;
  const actor = $('audit-actor').value;
  const rows = auditFilter(all, category, actor);
  $('audit').querySelector('tbody').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>${esc(formatTimestamp(r.occurredAtUtc))}</td><td>${esc(r.category)}</td><td>${esc(r.actor)}</td>` +
        `<td>${esc(r.action)}</td><td>${esc(r.targetType ?? '')}${esc(r.targetId ? `${r.targetId}` : '')}</td>` +
        `<td title="${esc(r.detail ?? '')}">${esc((r.detail ?? '').slice(0, 40))}</td></tr>`,
    )
    .join('');
  await refreshAuditCsv(category, actor);
}

/** The export route is API-key protected, so download it through fetch + blob instead of a bare link. */
async function refreshAuditCsv(category, actor) {
  const link = $('audit-csv');
  if (csvUrl) {
    URL.revokeObjectURL(csvUrl);
    csvUrl = '';
  }
  link.removeAttribute('href');
  try {
    const params = new URLSearchParams();
    if (category) params.set('category', category);
    if (actor) params.set('actor', actor);
    const query = params.toString();
    const response = await apiRaw(`/api/audit/export.csv${query ? `?${query}` : ''}`);
    if (!response.ok) return;
    const blob = await response.blob();
    csvUrl = URL.createObjectURL(blob);
    link.href = csvUrl;
  } catch {
    link.textContent = t('state.csvFailed');
  }
}

async function renderConfig() {
  const c = configCard(await api('/api/config'));
  $('auth-enabled').checked = c.authEnabled;
  $('lock-threshold').value = c.lockoutThreshold;
  $('lock-minutes').value = c.lockoutMinutes;
  $('retention-days').value = c.recordingRetentionDays;
  $('retention-watermark').value = c.recordingWatermarkGb;
  $('alarm-retention-days').value = c.alarmRetentionDays;
  const usage = await api('/api/config/usage').catch(() => null);
  $('storage-usage').textContent = usage
    ? t('retention.summary', { gb: usage.gb.toFixed(2), days: usage.retentionDays, alarmDays: usage.alarmRetentionDays, watermark: usage.watermarkGb })
    : '';
}

async function saveConfig(event) {
  event.preventDefault();
  try {
    await api('/api/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        authEnabled: $('auth-enabled').checked,
        lockoutThreshold: Number($('lock-threshold').value),
        lockoutMinutes: Number($('lock-minutes').value),
        recordingRetentionDays: Number($('retention-days').value),
        recordingWatermarkGb: Number($('retention-watermark').value),
        alarmRetentionDays: Number($('alarm-retention-days').value),
      }),
    });
    await renderConfig();
    $('config-msg').textContent = t('state.saved');
  } catch (err) {
    $('config-msg').textContent = String(err);
  }
}

async function runRetention() {
  try {
    const result = await api('/api/retention/run', { method: 'POST' });
    $('config-msg').textContent =
      t('retention.purgeDone', { age: result.agePurged, watermark: result.watermarkPurged, alarm: result.alarmPurged, freed: (result.bytesFreed / 1073741824).toFixed(2) }) +
      (result.skipped ? t('retention.purgeSkipped', { count: result.skipped }) : '');
    await renderConfig();
  } catch (err) {
    $('config-msg').textContent = String(err);
  }
}

async function renderAccounts() {
  const accounts = accountRows(await api('/api/accounts'));
  $('accounts').querySelector('tbody').innerHTML = accounts
    .map(
      (a) =>
        `<tr><td>${esc(a.username)}</td><td>${esc(a.role)}</td><td>${esc(a.enabled ? t('common.enabled') : t('common.disabled'))}${esc(a.locked ? t('common.lockedSuffix') : '')}</td>` +
        `<td><button data-toggle="${Number(a.id)}" data-enable="${esc(!a.enabled)}">${esc(a.enabled ? t('common.disabled') : t('common.enabled'))}</button>` +
        `<button data-role="${Number(a.id)}" data-next="${esc(a.role === 'admin' ? 'viewer' : 'admin')}">${tx('role.switchTo', { role: t(a.role === 'admin' ? 'role.viewer' : 'role.admin') })}</button>` +
        `<button data-del="${Number(a.id)}">${tx('action.delete')}</button></td></tr>`,
    )
    .join('');

  document.querySelectorAll('button[data-toggle]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      await api(`/api/accounts/${btn.dataset.toggle}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: btn.dataset.enable === 'true' }),
      });
      await renderAccounts();
    }),
  );
  document.querySelectorAll('button[data-role]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      await api(`/api/accounts/${btn.dataset.role}`, {
        method: 'PATCH',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ role: btn.dataset.next }),
      });
      await renderAccounts();
    }),
  );
  document.querySelectorAll('button[data-del]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      await api(`/api/accounts/${btn.dataset.del}`, { method: 'DELETE' });
      await renderAccounts();
    }),
  );
}

async function submitAccount(event) {
  event.preventDefault();
  const payload = JSON.stringify({
    username: $('new-user').value.trim(),
    password: $('new-pass').value,
    role: $('new-role').value,
  });
  try {
    await api('/api/accounts', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: payload });
    $('new-user').value = '';
    $('new-pass').value = '';
    $('account-msg').textContent = '';
  } catch (err) {
    $('account-msg').textContent = String(err);
  }
  await renderAccounts();
}

function renderMap() {
  const pinCount = 16;
  const pins = gridLayout(pinCount, 4);
  const el = $('map');
  el.innerHTML = '';
  for (const [i, pin] of pins.entries()) {
    const box = document.createElement('div');
    box.className = 'pin';
    Object.assign(box.style, pinStyles(pin));
    box.textContent = 'CAM';
    box.title = `CAM-${String(i + 1).padStart(2, '0')}`;
    el.appendChild(box);
  }
}

function renderSmartwall() {
  api('/api/smartwall/board')
    .then((body) => {
      const snap = smartwallSnapshot(Array.isArray(body) ? body : (body?.cells ?? []));
      $('sw').textContent = `smartwall ${snap.count} · critical ${snap.critical}`;
      $('sw').classList.toggle('hot', snap.critical > 0);
      const swPanel = $('sw-panel');
      if (swPanel) {
        swPanel.textContent = `smartwall ${snap.count} · critical ${snap.critical}`;
        swPanel.classList.toggle('hot', snap.critical > 0);
      }
      const el = $('swgrid');
      el.innerHTML = '';
      const cells = snap.cells.length ? snap.cells : [{}];
      const pins = focusLayout(cells, 4);
      cells.forEach((c, i) => {
        const box = document.createElement('div');
        box.className = `swtile ${tileClass(c)}`;
        Object.assign(box.style, pinStyles(pins[i], 0.01));
        const tile = tileLabel(c);
        box.title = t('tile.title', { channel: tile.channel, type: tile.type, priority: tile.priority });
        box.textContent = String(tile.channel);
        el.appendChild(box);
      });
    })
    .catch(() => {});
}

async function renderDaily() {
  const dayFrom = new Date();
  dayFrom.setUTCHours(0, 0, 0, 0);
  const dayTo = new Date(dayFrom.getTime() + 24 * 3600 * 1000);
  const params = new URLSearchParams({ from: dayFrom.toISOString(), to: dayTo.toISOString() });
  try {
    const card = dailyCard(await api(`/api/reports/daily?${params}`));
    $('daily-summary').textContent =
      t('recording.card', { hours: card.hours, gb: card.gb, disconnects: card.disconnects });
    $('daily-events').querySelector('tbody').innerHTML = card.events
      .map((e) => `<tr><td>${esc(e.type)}</td><td>${esc(e.count)}</td></tr>`)
      .join('');
  } catch (err) {
    $('daily-summary').textContent = String(err);
  }
}

async function renderSchedules() {
  const list = await gatedFetch('sched-msg', '/api/recording/schedules');
  if (!list) {
    $('sched-body').innerHTML = '';
    $('sched-count').textContent = '';
    return;
  }
  $('sched-body').innerHTML = list
    .map(
      (s) =>
        `<tr><td>${esc(s.channelId)}</td><td>${esc(scheduleLabel(s))}</td><td>${esc(s.enabled ? t('common.enabled') : t('common.disabled'))}</td>
         <td><button data-sched-del="${Number(s.id)}">${tx('action.delete')}</button></td></tr>`,
    )
    .join('');
  $('sched-count').textContent = t('count.parenthesized', { n: list.length });
  $('sched-body').querySelectorAll('button[data-sched-del]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      const resp = await apiRaw(`/api/recording/schedules/${btn.dataset.schedDel}`, { method: 'DELETE' }).catch(
        () => null,
      );
      if (await feedback('sched-msg', resp)) renderSchedules();
    }),
  );
}

function bindScheduleForm() {
  const form = $('sched-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const checked = [...form.querySelectorAll('input[name="sday"]:checked')].map((c) => Number(c.value));
    const start = form.querySelector('input[name="sstart"]').value.split(':');
    const end = form.querySelector('input[name="send"]').value.split(':');
    if (checked.length === 0) return;
    const body = {
      channelId: Number(form.querySelector('input[name="schan"]').value),
      daysMask: checked.reduce((m, d) => m | (1 << d), 0),
      startMinute: Number(start[0]) * 60 + Number(start[1]),
      endMinute: Number(end[0]) * 60 + Number(end[1]),
      enabled: true,
    };
    const resp = await apiRaw('/api/recording/schedules', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).catch(() => null);
    if (await feedback('sched-msg', resp)) renderSchedules();
  });
}

async function renderPatrols() {
  const list = await gatedFetch('patrol-msg', '/api/patrols');
  if (!list) {
    $('patrol-body').innerHTML = '';
    $('patrol-count').textContent = '';
    return;
  }
  $('patrol-body').innerHTML = list
    .map(
      (p) =>
        `<tr><td>${esc(patrolLabel(p))}</td><td>${esc(p.enabled ? t('common.enabled') : t('common.disabled'))}</td>
         <td><button data-patrol-del="${Number(p.id)}">${tx('action.delete')}</button></td></tr>`,
    )
    .join('');
  $('patrol-count').textContent = t('count.parenthesized', { n: list.length });
  $('patrol-body').querySelectorAll('button[data-patrol-del]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      const resp = await apiRaw(`/api/patrols/${btn.dataset.patrolDel}`, { method: 'DELETE' }).catch(() => null);
      if (await feedback('patrol-msg', resp)) renderPatrols();
    }),
  );
}

function bindPatrolForm() {
  const form = $('patrol-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const body = {
      name: form.querySelector('input[name="pname"]').value.trim(),
      channelId: Number(form.querySelector('input[name="pchan"]').value),
      enabled: form.querySelector('input[name="penabled"]').checked,
      windowStart: form.querySelector('input[name="pstart"]').value || '00:00',
      windowEnd: form.querySelector('input[name="pend"]').value || '23:59',
      steps: [],
    };
    if (!body.name) return;
    const resp = await apiRaw('/api/patrols', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).catch(() => null);
    if (await feedback('patrol-msg', resp)) renderPatrols();
  });
}

async function renderEvidence() {
  const rows = evRows(await api('/api/evidence').catch(() => []));
  $('evidence-body').innerHTML = rows
    .map(
      (m) =>
        `<tr><td>#${esc(m.id)}</td><td>${esc(m.status)}</td><td>${esc(m.createdAt?.slice(0, 19) ?? '')}</td><td>${esc(m.items)}</td></tr>`,
    )
    .join('');
  $('evidence-count').textContent = t('count.parenthesized', { n: rows.length });
}

async function renderBackup() {
  const rows = backupRows(await api('/api/backup/runs').catch(() => []));
  $('backup-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${esc(r.seq)}</td><td>${esc(r.runAt?.slice(0, 19) ?? '')}</td><td>${esc(r.copied)}</td><td>${esc(r.bytes)}</td><td>${esc(r.failed)}</td><td>${esc(r.advanced ? t('common.advanced') : t('common.notAdvanced'))}</td><td class="muted">${esc(r.target)}</td></tr>`,
    )
    .join('');
  $('backup-count').textContent = t('count.parenthesized', { n: rows.length });
}

function bindBackupForm() {
  const form = $('backup-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const src = form.querySelector('input[name="bsrc"]').value.trim();
    const dst = form.querySelector('input[name="bdst"]').value.trim();
    if (!src || !dst) return;
    const resp = await apiRaw('/api/backup/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sourceRoot: src, targetRoot: dst }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('backup-msg').textContent = resp.ok
      ? t('backup.done', { scanned: body.scanned, copied: body.copied, size: (body.copiedBytes / 1048576).toFixed(1), failed: body.failed })
      : body?.error ?? t('state.failed');
    if (resp.ok) renderBackup();
  });
}

async function renderDoor() {
  const card = (document.getElementById('door-card')?.value ?? '').trim();
  const ok = document.getElementById('door-ok')?.checked ?? false;
  const denied = document.getElementById('door-denied')?.checked ?? false;
  const q = new URLSearchParams();
  if (card) q.set('card', card);
  if (ok !== denied) q.set('granted', String(ok));
  const rows = doorRows(await api(`/api/door/events?${q}`).catch(() => []));
  $('door-body').innerHTML = rows
    .map(
      (e) =>
        `<tr><td>${esc(e.time)}</td><td>${esc(e.device)}</td><td>${esc(e.door)}</td><td>${esc(e.card)}</td><td>${esc(e.direction)}</td><td class="${esc(e.granted ? 'ok' : 'bad')}">${esc(e.granted ? t('common.granted') : t('common.denied'))}</td><td class="muted">${esc(e.reason)}</td></tr>`,
    )
    .join('');
  $('door-count').textContent = t('count.parenthesized', { n: rows.length });
}

async function renderDetections() {
  const conf = (document.getElementById('det-conf')?.value ?? '').trim();
  const cls = (document.getElementById('det-class')?.value ?? '').trim();
  const q = new URLSearchParams();
  if (conf && Number(conf) > 0) q.set('minConfidence', conf);
  if (cls) q.set('class', cls);
  // 被閘門擋下時要清空並留白字數，而不是畫成 0 筆——「沒偵測到」與「沒授權」講法不同。
  const list = await gatedFetch('det-msg', `/api/detections?${q}`);
  if (!list) {
    $('det-body').innerHTML = '';
    $('det-count').textContent = '';
    $('det-summary').textContent = '';
    return;
  }
  const rows = detRows(list);
  $('det-body').innerHTML = rows
    .map(
      (d) =>
        `<tr><td>${esc(d.time)}</td><td>ch${esc(d.channel)}</td><td>${esc(d.cls)}</td><td>${esc((d.conf * 100).toFixed(0))}%</td><td>${esc(d.x.toFixed(2))},${esc(d.y.toFixed(2))}</td><td>${esc(d.box)}</td></tr>`,
    )
    .join('');
  $('det-count').textContent = t('count.parenthesized', { n: rows.length });
  const summaryUrl = q.toString() ? `/api/detections/summary?${q}` : '/api/detections/summary';
  const summary = await api(summaryUrl).catch(() => []);
  $('det-summary').textContent = (summary ?? [])
    .map((s) => `${s.class}×${s.count}`)
    .join(' · ');
}

async function renderNotifs() {
  const rows = notifRows(await api('/api/notifications?limit=50').catch(() => []));
  $('notif-body').innerHTML = rows
    .map(
      (n) =>
        `<tr><td>${esc(n.time)}</td><td>ch${esc(n.channel)}</td><td>${esc(n.event)}</td><td>${esc(n.route)}</td><td class="${esc(n.ok ? 'ok' : 'bad')}">${esc(n.ok ? t('state.ok') : t('state.failed'))}</td><td>${esc(n.attempts)}</td><td class="muted">${esc(n.detail)}</td></tr>`,
    )
    .join('');
  $('notif-count').textContent = t('count.parenthesized', { n: rows.length });
}

async function renderExports() {
  const rows = exportRows(await api('/api/exports').catch(() => []));
  $('export-body').innerHTML = rows
    .map(
      (j) =>
        `<tr><td>#${esc(j.id)}</td><td>ch${esc(j.channel)}</td><td>${esc(j.stream)}</td><td>${esc(j.start)}</td><td>${esc(j.end)}</td><td>${esc(j.status)}</td><td>${esc(j.file ?? '')}</td><td class="muted">${esc(j.sha)}${esc(j.error ? ` · ${j.error}` : '')}</td></tr>`,
    )
    .join('');
  $('export-count').textContent = t('count.parenthesized', { n: rows.length });
}

function bindExportForm() {
  const form = $('export-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const ch = Number(form.querySelector('input[name="ech"]').value);
    const from = form.querySelector('input[name="efrom"]').value;
    const to = form.querySelector('input[name="eto"]').value;
    if (!ch || !from || !to) return;
    const resp = await apiRaw('/api/exports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ channelId: ch, stream: 'main', fromUtc: new Date(from).toISOString(), toUtc: new Date(to).toISOString() }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('export-msg').textContent = resp.ok ? t('export.queued', { id: body.id }) : body?.error ?? t('state.failed');
    if (resp.ok) renderExports();
  });
}

async function renderProviders() {
  const list = await gatedFetch('provider-msg', '/api/auth/providers');
  if (!list) {
    $('provider-body').innerHTML = '';
    $('provider-count').textContent = '';
    return;
  }
  const rows = providerRows(list);
  $('provider-body').innerHTML = rows
    .map(
      (p) =>
        `<tr><td>#${esc(p.id)}</td><td>${esc(p.name)}</td><td>${esc(p.kind)}</td><td><input type="checkbox" data-provider-toggle="${Number(p.id)}" ${esc(p.enabled ? 'checked' : '')}></td><td><button class="danger" data-provider-del="${Number(p.id)}">${tx('action.delete')}</button></td><td class="muted">${esc(p.config.slice(0, 40))}</td></tr>`,
    )
    .join('');
  $('provider-count').textContent = t('count.parenthesized', { n: rows.length });
  Array.from(document.querySelectorAll('[data-provider-toggle]')).forEach((cb) => {
    cb.addEventListener('change', async () => {
      const resp = await apiRaw(`/api/auth/providers/${cb.dataset.providerToggle}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: cb.checked }),
      }).catch(() => null);
      // 被擋下就把勾選還原；否則畫面會停在一個伺服器根本沒接受的狀態。
      if (!(await feedback('provider-msg', resp))) {
        cb.checked = !cb.checked;
        return;
      }
      renderProviders();
    });
  });
  Array.from(document.querySelectorAll('[data-provider-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const resp = await apiRaw(`/api/auth/providers/${btn.dataset.providerDel}`, { method: 'DELETE' }).catch(() => null);
      if (await feedback('provider-msg', resp)) renderProviders();
    });
  });
}

function bindProviderForm() {
  const form = $('provider-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const name = form.querySelector('input[name="pname"]').value.trim();
    const kind = form.querySelector('select[name="pkind"]').value;
    const cfg = form.querySelector('textarea[name="pcfg"]').value.trim();
    if (!name || !cfg) return;
    const resp = await apiRaw('/api/auth/providers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, kind, configJson: cfg, enabled: true }),
    }).catch(() => null);
    if (await feedback('provider-msg', resp, (b) => t('common.added', { id: b.id }))) renderProviders();
  });
}

async function renderHolds() {
  const rows = holdRows(await api('/api/legal-holds').catch(() => []));
  $('hold-body').innerHTML = rows
    .map(
      (h) =>
        `<tr><td>#${esc(h.id)}</td><td>ch${esc(h.channel)}</td><td>${esc(h.from)}</td><td>${esc(h.to)}</td><td>${esc(h.reason)}</td><td>${esc(h.by)}</td><td class="${esc(h.active ? 'ok' : 'bad')}">${esc(h.active ? t('common.active') : t('common.revoked'))}</td><td class="muted">${esc(h.revokedBy)}</td><td>${esc(h.active ? `<button class="danger" data-hold-revoke="${Number(h.id)}">${tx('action.revoke')}</button>` : '')}</td></tr>`,
    )
    .join('');
  $('hold-count').textContent = t('count.parenthesized', { n: rows.length });
  Array.from(document.querySelectorAll('[data-hold-revoke]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const reason = prompt(t('state.revokeReason'));
      if (reason === null) return;
      await apiRaw(`/api/legal-holds/${btn.dataset.holdRevoke}/revoke`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ by: readSession()?.name ?? 'operator', reason }),
      });
      renderHolds();
    });
  });
}

function bindHoldForm() {
  const form = $('hold-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const ch = Number(form.querySelector('input[name="hch"]').value);
    const from = form.querySelector('input[name="hfrom"]').value;
    const to = form.querySelector('input[name="hto"]').value;
    const reason = form.querySelector('input[name="hreason"]').value.trim();
    if (!ch || !from || !to || !reason) return;
    const resp = await apiRaw('/api/legal-holds', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        channelId: ch,
        fromUtc: new Date(from).toISOString(),
        toUtc: new Date(to).toISOString(),
        reason,
        createdBy: readSession()?.name ?? 'operator',
      }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('hold-msg').textContent = resp.ok ? t('hold.created', { id: body.id }) : body?.error ?? t('state.failed');
    if (resp.ok) renderHolds();
  });
}

async function renderRules() {
  const rows = ruleRows(await api('/api/alert-rules').catch(() => []));
  $('rule-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${esc(r.id)}</td><td>${esc(r.name)}</td><td>${esc(r.event || '—')}</td><td>${esc(r.channel || '—')}</td><td>${esc(r.keyword || '—')}</td><td>${esc(r.channels || '—')}</td><td><input type="checkbox" data-rule-toggle="${Number(r.id)}" ${esc(r.enabled ? 'checked' : '')}></td><td>${esc(r.min)}</td><td><button class="danger" data-rule-del="${Number(r.id)}">${tx('action.delete')}</button></td></tr>`,
    )
    .join('');
  $('rule-count').textContent = t('count.parenthesized', { n: rows.length });
  Array.from(document.querySelectorAll('[data-rule-toggle]')).forEach((cb) => {
    cb.addEventListener('change', async () => {
      await apiRaw(`/api/alert-rules/${cb.dataset.ruleToggle}/enabled`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: cb.checked }),
      });
      renderRules();
    });
  });
  Array.from(document.querySelectorAll('[data-rule-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await apiRaw(`/api/alert-rules/${btn.dataset.ruleDel}`, { method: 'DELETE' });
      renderRules();
    });
  });
}

function bindRuleForm() {
  const form = $('rule-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const name = form.querySelector('input[name="rname"]').value.trim();
    const eventType = form.querySelector('input[name="revent"]').value.trim();
    const channel = Number(form.querySelector('input[name="rch"]').value || 0);
    const keyword = form.querySelector('input[name="rkw"]').value.trim();
    const min = Number(form.querySelector('input[name="rmin"]').value || 1);
    if (!name) return;
    const resp = await apiRaw('/api/alert-rules', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        name,
        eventType: eventType || null,
        channelId: channel || null,
        keyword: keyword || null,
        channels: null,
        matchEventTypes: null,
        frameMinutes: 0,
        minEventsInWindow: min,
      }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('rule-msg').textContent = resp.ok ? t('common.added', { id: body.id }) : body?.error ?? t('state.failed');
    if (resp.ok) renderRules();
  });
}

async function renderShares() {
  const list = await gatedFetch('share-msg', '/api/shares');
  if (!list) {
    $('share-body').innerHTML = '';
    $('share-count').textContent = '';
    return;
  }
  const rows = shareRows(list);
  $('share-body').innerHTML = rows
    .map(
      (s) =>
        `<tr><td>#${esc(s.id)}</td><td>${esc(s.kind)}</td><td>${esc(s.label || '—')}</td><td class="muted">${esc(s.path)}</td><td>${esc(s.token)}…</td><td>${esc(s.uses)}</td><td class="${esc(s.active ? 'ok' : 'bad')}">${esc(s.active ? t('common.valid') : t('common.invalid'))}</td><td>${esc(s.active ? `<button class="danger" data-share-revoke="${Number(s.id)}">${tx('action.revoke')}</button>` : '')}</td></tr>`,
    )
    .join('');
  $('share-count').textContent = t('count.parenthesized', { n: rows.length });
  Array.from(document.querySelectorAll('[data-share-revoke]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const resp = await apiRaw(`/api/shares/${btn.dataset.shareRevoke}/revoke`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      }).catch(() => null);
      if (await feedback('share-msg', resp)) renderShares();
    });
  });
}

function bindShareForm() {
  const form = $('share-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const kind = form.querySelector('select[name="skind"]').value;
    const path = form.querySelector('input[name="spath"]').value.trim();
    const label = form.querySelector('input[name="slabel"]').value.trim();
    const maxUses = Number(form.querySelector('input[name="smax"]').value || 0);
    if (!path) return;
    const resp = await apiRaw('/api/shares', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, resourcePath: path, label: label || null, maxUses }),
    }).catch(() => null);
    if (await feedback('share-msg', resp, (b) => t('share.created', { token: b.token.slice(0, 12) }))) renderShares();
  });
}

async function renderReds() {
  const rows = redRows(await api('/api/redactions?limit=200').catch(() => []));
  $('red-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${esc(r.id)}</td><td>${esc(r.source)}</td><td>${esc(r.ref)}</td><td>CH${esc(r.channel)}</td><td>${esc(r.time)}</td><td>${esc(r.rect)}</td><td class="${esc(r.filled ? 'ok' : '')}">${esc(r.filled ? t('common.filled') : t('common.boxed'))}</td><td><button class="danger" data-red-del="${Number(r.id)}">${tx('action.remove')}</button></td></tr>`,
    )
    .join('');
  $('red-count').textContent = t('count.parenthesized', { n: rows.length });
  Array.from(document.querySelectorAll('[data-red-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await apiRaw(`/api/redactions/${btn.dataset.redDel}`, { method: 'DELETE' });
      renderReds();
    });
  });
}

function bindRedForm() {
  const form = $('red-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const f = form.elements;
    const payload = {
      sourceType: f.rsource.value,
      refId: Number(f.rref.value || 0),
      channelId: Number(f.rchannel.value || 1),
      occurredAtUtc: f.rtime.value ? new Date(f.rtime.value).toISOString() : new Date().toISOString(),
      x: Number(f.rx.value || 0),
      y: Number(f.ry.value || 0),
      width: Number(f.rw.value || 0),
      height: Number(f.rh.value || 0),
      filled: f.rfilled.checked,
    };
    const resp = await apiRaw('/api/redactions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('red-msg').textContent = resp.ok ? t('common.added', { id: body.id }) : body?.error ?? t('state.failed');
    if (resp.ok) renderReds();
  });
}

async function renderRep() {
  const rows = repRows(await api('/api/replication').catch(() => []));
  $('rep-body').innerHTML = rows
    .map(
      (j) =>
        `<tr><td>#${esc(j.id)}</td><td class="muted">${esc(j.src)}</td><td class="muted">${esc(j.dst)}</td><td>${esc(j.minutes)} ${tx('col.minutes')}</td><td class="${esc(j.enabled ? 'ok' : '')}">${esc(j.enabled ? t('common.enabled') : t('common.disabled'))}</td><td class="${esc(j.fails > 0 ? 'bad' : 'ok')}">${esc(j.lastResult || '—')}${esc(j.fails > 0 ? `（連續 ${j.fails} 次）` : '')}</td><td class="${esc(j.due ? 'bad' : '')}">${esc(j.due ? t('common.due') : '—')}</td></tr>`,
    )
    .join('');
  $('rep-count').textContent = t('count.parenthesized', { n: rows.length });
}

async function renderHealth() {
  const health = await api('/api/system-metrics').catch(() => null);
  if (!health) {
    $('sys-msg').textContent = t('state.metricsUnavailable');
    return;
  }
  const rows = sysMetricsRows(health);
  $('sys-count').textContent = t('metrics.disks', { count: rows.disks.length });
  $('sys-kv').textContent = 
    t('metrics.exec', { minutes: rows.uptimeMinutes, workingSet: rows.workingSetMb, cpu: rows.cpuPercent });
  $('sys-body').innerHTML = rows.disks
    .map(
      (d) =>
        `<tr><td>${esc(d.name)}</td><td>${esc(d.format)}</td><td>${esc(d.totalMb)} MB</td>` +
        `<td>${esc(d.freeMb)} MB</td><td>${tx('metrics.freePct', { pct: (100 - d.usedPct).toFixed(1) })}</td></tr>`,
    )
    .join('');
  $('sys-msg').textContent = '';
}

async function renderBoard() {
  const [board, summary] = await Promise.all([
    api('/api/alarm-board?take=200').catch(() => []),
    api('/api/alarm-board/summary').catch(() => null),
  ]);
  const rows = alarmRows(board);
  const shown = filterBoardRows(rows, boardFilter);
  const can = canAct(readSession()?.role);
  $('kpi').textContent = t('board.ackRate', { pct: ackRate(rows), count: rows.filter((r) => r.priority === 'critical').length });
  $('alarm-body').innerHTML = shown
    .map(
      (b) =>
        `<tr class="${esc(b.overdue ? 'overdue' : '')}"><td>#${esc(b.id)}</td><td>CH${esc(b.channel)}</td><td>${esc(b.event)}</td><td>${esc(b.start)}</td>` +
        `<td>${esc(b.priority)}</td><td>${esc(b.status)}</td><td class="${esc(b.overdue ? 'bad' : '')}">${esc(b.overdue ? t('common.overdue') : '—')}</td>` +
        (can
          ? `<td><select data-pri="${Number(b.id)}"><option value="low" ${esc(b.priority === 'low' ? 'selected' : '')}>${tx('priority.low')}</option><option value="normal" ${esc(b.priority === 'normal' ? 'selected' : '')}>${tx('priority.normal')}</option><option value="high" ${esc(b.priority === 'high' ? 'selected' : '')}>${tx('priority.high')}</option><option value="critical" ${esc(b.priority === 'critical' ? 'selected' : '')}>${tx('priority.critical')}</option></select><button data-triage="${Number(b.id)}">${tx('action.triage')}</button><button data-ack="${Number(b.id)}" class="${esc(b.status === 'acknowledged' ? 'ok' : '')}">${tx('action.confirm')}</button><button class="danger" data-fa="${Number(b.id)}">${tx('common.falseAlarm')}</button></td>`
          : ''),
    )
    .join('');
  const counts = {
    '': rows.length,
    pending: summary?.Pending ?? 0,
    acknowledged: summary?.Acknowledged ?? 0,
    actioned: summary?.Actioned ?? 0,
    false_alarm: summary?.FalseAlarm ?? 0,
    overdue: summary?.Overdue ?? 0,
  };
  const chips = [
    [t('common.all'), ''],
    [t('common.pending'), 'pending'],
    [t('common.acknowledged'), 'acknowledged'],
    [t('common.actioned'), 'actioned'],
    [t('common.falseAlarm'), 'false_alarm'],
    [t('common.overdue'), 'overdue'],
  ];
  $('alarm-count').textContent = boardFilter
    ? t('count.filteredOfTotal', { shown: shown.length, total: rows.length })
    : t('count.parenthesized', { n: rows.length });
  $('alarm-meta').textContent = t('board.updatedAt', { time: new Date().toLocaleTimeString(locale()) });
  $('alarm-badges').innerHTML = chips
    .map(
      ([label, key]) =>
        `<button class="chip-btn${esc(boardFilter === key ? ' on' : '')}" data-board-filter="${esc(key)}" aria-pressed="${esc(boardFilter === key)}">${tx('board.chip', { label, count: counts[key] })}</button>`,
    )
    .join('');
  Array.from(document.querySelectorAll('[data-board-filter]')).forEach((btn) => {
    btn.addEventListener('click', () => {
      boardFilter = btn.dataset.boardFilter;
      renderBoard();
    });
  });
  if (can) bindBoardActions();
}

function bindBoardActions() {
  Array.from(document.querySelectorAll('[data-triage]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.triage;
      const priority = document.querySelector(`[data-pri="${id}"]`).value;
      await apiRaw(`/api/alarm-board/${id}/triage`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ priority, dueUtc: null, owner: null }),
      });
      renderBoard();
    });
  });
  Array.from(document.querySelectorAll('[data-ack]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.ack;
      await apiRaw(`/api/alarm-board/${id}/ack`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ acknowledged: true }),
      });
      renderBoard();
    });
  });
  Array.from(document.querySelectorAll('[data-fa]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.fa;
      await apiRaw(`/api/alarm-board/${id}/disposition`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: 'false_alarm', assignedTo: null, note: t('board.manageFalseAlarm') }),
      });
      renderBoard();
    });
  });
}

function bindEvidenceForm() {
  const form = $('evidence-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const name = form.querySelector('input[name="evname"]').value.trim();
    const files = form
      .querySelector('textarea[name="evfiles"]')
      .value.split('\n')
      .map((f) => f.trim())
      .filter(Boolean);
    if (!name || files.length === 0) return;
    const resp = await apiRaw('/api/evidence/package', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bundleName: name, files }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('evidence-msg').textContent = resp.ok
      ? t('evidence.bundleCreated', { path: body.bundlePath, sha: body.bundleSha256?.slice(0, 12) })
      : body?.error ?? t('state.failed');
    if (resp.ok) renderEvidence();
  });
}

function renderPos() {
  const to = new Date();
  const from = new Date(to.getTime() - 3600 * 1000);
  const params = new URLSearchParams({ from: from.toISOString(), to: to.toISOString(), limit: '200' });
  api(`/api/pos?${params}`)
    .then((page) => {
      const t = posTotals(page.items);
      const regs = Object.entries(t.registers)
        .map(([id, r]) => `${id}:${r.count}`)
        .join(' ');
      $('pos').textContent = `POS ${t.count} · $${(t.totalCents / 100).toFixed(2)} · ${regs}`;
    })
    .catch(() => {});
}

// 必須是函式而不是模組層級常數：常數會在 import 時就把譯文定死，切換語言後這張表不會更新。
const FORENSIC_SOURCE_NAMES = { 1: () => t('replay.sourceAlarm'), 2: () => t('replay.source'), 4: () => 'POS', 8: () => t('replay.sourceEdgeAi') };

async function searchEvents(q) {
  if (!q) {
    $('events-msg').textContent = t('state.needKeyword');
    return;
  }
  const to = new Date();
  const from = new Date(to.getTime() - 24 * 3600 * 1000);
  const params = new URLSearchParams({ q, from: from.toISOString(), to: to.toISOString(), limit: '50' });
  const page = await api(`/api/events/search?${params}`);
  const items = page.items ?? [];
  const can = canAct(readSession()?.role);
  $('events').querySelector('tbody').innerHTML = items
    .map((e) => {
      const row = eventRow(e);
      const sid = Number(e.SourceId ?? e.sourceId ?? 0);
      const source = Number(e.Source ?? e.source ?? 0);
      return (
        `<tr><td>${esc(sid ? '#' + sid : '')}</td><td>${esc(row.time)}</td><td>${esc(FORENSIC_SOURCE_NAMES[source]?.() ?? '—')}</td><td>${esc(row.type)}</td><td>${esc(row.status || '—')}</td>` +
        (can && source === 1
          ? `<td><button data-sack="${Number(sid)}">${tx('action.confirm')}</button><button class="danger" data-sfa="${Number(sid)}">${tx('common.falseAlarm')}</button></td></tr>`
          : '<td></td></tr>')
      );
    })
    .join('');
  if (can) bindSearchActions(() => searchEvents(q));
  if (items.length === 0) {
    $('events-msg').textContent = t('state.noResults');
  } else if (items.length < page.total) {
    $('events-msg').textContent = t('search.foundTruncated', { total: page.total, shown: items.length });
  } else {
    $('events-msg').textContent = t('search.found', { total: page.total });
  }
}

function bindSearchActions(refresh) {
  Array.from(document.querySelectorAll('[data-sack]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await apiRaw(`/api/alarm-board/${btn.dataset.sack}/ack`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ acknowledged: true }),
      });
      refresh();
      renderBoard();
    });
  });
  Array.from(document.querySelectorAll('[data-sfa]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await apiRaw(`/api/alarm-board/${btn.dataset.sfa}/disposition`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: 'false_alarm', assignedTo: null, note: t('board.searchFalseAlarm') }),
      });
      refresh();
      renderBoard();
    });
  });
}

let timelineDay = '';

function timelineDayUtc() {
  const day = new Date();
  if (timelineDay) {
    const [y, m, d] = timelineDay.split('-').map(Number);
    day.setUTCFullYear(y, m - 1, d);
  }
  day.setUTCHours(0, 0, 0, 0);
  return day;
}

function bumpTimelineDay(delta) {
  const base = timelineDayUtc();
  base.setUTCDate(base.getUTCDate() + delta);
  timelineDay = base.toISOString().slice(0, 10);
  $('timeline-day').value = timelineDay;
  refreshTimeline();
}

async function refreshTimeline() {
  const day = timelineDayUtc();
  const timeline = await api(buildTimelineQuery({ channelId: 1, stream: 'main', day }));
  $('gap').textContent = gapLabel(timeline.gapFraction);
  const el = $('timeline');
  el.innerHTML = '';
  for (const bar of timeline.bars) {
    const div = document.createElement('div');
    div.className = 'bar';
    div.style.left = `${escPct(bar.leftFraction * 100)}%`;
    div.style.width = `${escPct(Math.max(bar.widthFraction * 100, 0.2))}%`;
    el.appendChild(div);
  }
  for (const marker of timeline.markers) {
    const div = document.createElement('div');
    div.className = 'marker';
    div.style.left = `${escPct(marker.xFraction * 100)}%`;
    el.appendChild(div);
  }
}

/**
 * 即時監看（M244，§14.3 串流 P0）：WHEP → 原生 RTCPeerConnection。
 * 與遠程回放共用同一塊「單一認證傳輸層」apiRaw，API key 只從 Authorization 標頭送出。
 */
const liveViewer = createLiveViewer({ apiRaw, t });

async function openLiveView(event) {
  event.preventDefault();
  const channelId = Number($('live-channel').value);
  const msg = $('live-msg');
  const started = await liveViewer.open(channelId, $('live-video'), (text) => {
    msg.textContent = text;
  });

  $('live-meta').textContent = started
    ? t('live.viewerCount', { channel: channelId, viewers: 1 })
    : '';
}

async function stopLiveView() {
  const stopped = await liveViewer.stop();
  if (stopped) {
    $('live-msg').textContent = t('state.liveStopped');
    $('live-meta').textContent = '';
  }
}

/**
 * 遠程回放（M239，§14.3 串流 P0）：HLS VOD → MediaSource。
 * 伺服器每個錄影檔自帶 ftyp＋moov，所以初始化段只送第一段的那一份，
 * 之後把每段的 moof/mdat 依序 append 就能在瀏覽器裡播。
 */
async function attachPlaylist(video, playlist) {
  if (!('MediaSource' in window)) {
    throw new Error(t('state.mseUnsupported'));
  }

  const type = mseMimeType(
    video.canPlayType('video/mp4; codecs="avc1.64001f,mp4a.40.2"'),
    video.canPlayType('video/mp4; codecs="avc1.42E01E"'),
  );

  const media = new MediaSource();
  video.src = URL.createObjectURL(media);
  try {
    await new Promise((resolve) => media.addEventListener('sourceopen', resolve, { once: true }));
    const buffer = media.addSourceBuffer(type);
    const push = async (uri) => {
      const response = await apiRaw(uri);
      if (!response.ok) throw new Error(`${uri} -> ${response.status}`);
      const bytes = await response.arrayBuffer();
      await new Promise((resolve, reject) => {
        buffer.addEventListener('updateend', resolve, { once: true });
        buffer.addEventListener('error', reject, { once: true });
        buffer.appendBuffer(bytes);
      });
    };

    if (playlist.initUri) await push(playlist.initUri);
    for (const segment of playlist.segments) await push(segment.uri);
    media.endOfStream();
  } finally {
    URL.revokeObjectURL(video.src);
  }
}

async function playRemote(event) {
  event.preventDefault();
  const msg = $('play-msg');
  const channelId = Number.parseInt($('play-channel').value, 10);
  const from = new Date($('play-from').value);
  const to = new Date($('play-to').value);
  if (!Number.isFinite(channelId) || channelId <= 0) {
    msg.textContent = t('state.channelPositive');
    return;
  }

  if (!Number.isFinite(from.getTime()) || !Number.isFinite(to.getTime()) || to <= from) {
    msg.textContent = t('state.needRange');
    return;
  }

  msg.textContent = t('state.loading');
  try {
    const response = await apiRaw(buildPlaylistQuery({ channelId, stream: 'main', from, to }));
    if (!response.ok) {
      const info = await response.json().catch(() => null);
      msg.textContent = info?.error ?? t('state.playlistFailed', { status: response.status });
      return;
    }

    const playlist = parseM3u8(await response.text());
    if (!playlist || playlist.segments.length === 0) {
      msg.textContent = t('state.noSegments');
      return;
    }

    await attachPlaylist($('play-video'), playlist);
    $('play-meta').textContent = t('replay.playlistMeta', { segments: playlist.segments.length, seconds: Math.round(playlist.duration) });
    msg.textContent = t('state.ready');
  } catch (err) {
    msg.textContent = err.message;
  }
}

function defaultPlayWindow() {
  const now = new Date();
  const from = new Date(now);
  from.setUTCHours(Math.max(0, now.getUTCHours() - 1), 0, 0, 0);
  const to = new Date(from.getTime() + 60 * 60 * 1000);
  const local = (d) => new Date(d.getTime() - d.getTimezoneOffset() * 60000).toISOString().slice(0, 16);
  $('play-from').value = local(from);
  $('play-to').value = local(to);
}

function connectLive() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  const socket = new WebSocket(`${proto}://${location.host}/api/alerts/ws?key=${encodeURIComponent(key())}`);
  socket.onopen = () => {
    $('live').textContent = 'WS on';
    $('live').classList.replace('off', 'on');
  };
  socket.onmessage = (ev) => {
    const msg = parseWsMessage(ev.data);
    if (!msg) return;
    const li = document.createElement('li');
    li.textContent = `${formatTimestamp(new Date().toISOString())} ${msg.kind} #${msg.eventId} ${msg.priority ?? msg.status ?? ''}`;
    $('live-log').prepend(li);
    if (msg.kind.startsWith('alarm.')) renderSmartwall();
  };
  socket.onclose = () => {
    $('live').textContent = 'WS off';
    $('live').classList.remove('on');
  };
}

function wire() {
  $('login').addEventListener('submit', submitLogin);
  $('logout').addEventListener('click', (e) => {
    e.preventDefault();
    logout();
  });
  $('timeline-day').addEventListener('change', refreshTimeline);
  $('timeline-up').addEventListener('click', () => bumpTimelineDay(1));
  $('timeline-down').addEventListener('click', () => bumpTimelineDay(-1));
  $('play-form').addEventListener('submit', playRemote);
  $('live-form').addEventListener('submit', openLiveView);
  $('live-stop').addEventListener('click', stopLiveView);
  defaultPlayWindow();
  const saved = localStorage.getItem('helivms.apiKey');
  if (saved) $('apikey').value = saved;
  $('apikey').addEventListener('change', () => {
    const value = $('apikey').value.trim();
    if (value) {
      localStorage.setItem('helivms.apiKey', value);
    } else {
      localStorage.removeItem('helivms.apiKey');
    }
    location.reload();
  });
  $('audit-category').addEventListener('change', renderAudit);
  $('audit-actor').addEventListener('input', renderAudit);
  $('account-form').addEventListener('submit', submitAccount);
  $('config-form').addEventListener('submit', saveConfig);
  $('retention-run').addEventListener('click', runRetention);
  $('sys-refresh')?.addEventListener('click', renderHealth);
  $('board-refresh')?.addEventListener('click', () => renderBoard());
  bindEvidenceForm();
  bindBackupForm();
  bindExportForm();
  bindProviderForm();
  bindHoldForm();
  bindRuleForm();
  bindShareForm();
  bindRedForm();
  $('search-form').addEventListener('submit', (e) => {
    e.preventDefault();
    searchEvents($('q').value || '*').catch(console.error);
  });
  $('apikey').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      refreshAll();
    }
  });
  ['door-card', 'door-ok', 'door-denied'].forEach((id) => {
    const el = $(id);
    if (el) el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', renderDoor);
  });
  ['det-conf', 'det-class'].forEach((id) => {
    const el = $(id);
    if (el) el.addEventListener('input', renderDetections);
  });
}

/** 語系切換（M243）。選擇記在 localStorage，重載後沿用。 */
const LOCALE_KEY = 'helivms.locale';

function storedLocale() {
  try {
    return window.localStorage.getItem(LOCALE_KEY) ?? DEFAULT_LOCALE;
  } catch {
    // 隱私模式或被停用儲存時仍要能正常操作，只是每次重載回到預設語系。
    return DEFAULT_LOCALE;
  }
}

/**
 * 套用語系。除了靜態標記（applyDom），表格與訊息列也必須重繪——
 * 它們是執行時用 t()/tx() 產生的，不會因為 applyDom 而改變。
 * 直接呼叫各 render 而不整頁重載，是因為重載會清掉已輸入的篩選條件與分頁狀態。
 */
async function applyLocale(next, { persist = true } = {}) {
  const applied = setLocale(next);
  if (persist) {
    try {
      window.localStorage.setItem(LOCALE_KEY, applied);
    } catch {
      /* 儲存不可用時忽略：語系仍然生效，只是不會被記住 */
    }
  }

  applyDom(document);
  const pick = $('locale');
  if (pick) pick.value = applied;

  updateChrome();
  await refreshLicense();
  await refreshAll();
  return applied;
}

function bindLocalePicker() {
  const pick = $('locale');
  if (!pick) return;
  pick.value = locale();
  pick.addEventListener('change', () => {
    void applyLocale(pick.value);
  });
}

async function boot() {
  setLocale(storedLocale());
  applyDom(document);
  const pick = $('locale');
  if (pick) pick.value = locale();

  wire();
  updateChrome();
  await refreshHealth();
  await refreshLicense();
  await refreshAll();
  bindScheduleForm();
  bindPatrolForm();
  bindLocalePicker();
  connectLive();
  setInterval(() => {
    if (pollGate(document.activeElement?.tagName, document.hidden)) {
      refreshLicense();
      refreshAll();
    }
  }, POLL_MS);
}

/**
 * 授權狀態徽章（M210）。旗標閘門對未授權端點回 403，而 HTTP 錯誤不會觸發面板的 .catch，
 * 未裝示的話使用者只會看到一片空面板。改由這裡主動取一次狀態並說明缺哪幾項。
 * 一律用 textContent，授權訊息不得以 innerHTML 插入。
 */
async function refreshLicense() {
  const el = $('lic');
  try {
    const res = await apiRaw('/api/license');
    if (!res.ok) {
      if (el) el.textContent = '';
      return;
    }
    const status = await res.json();
    if (!el) return;
    if (status.decision === 'Valid') {
      // 續期提醒（§19.4 到期前 14 天）由伺服端算好文案，前端不重寫一份。
      el.textContent = status.expiryMessage
        ? t('lic.channelsExpiry', { count: status.maxCameras, expiry: status.expiryMessage })
        : t('lic.channels', { count: status.maxCameras });
      el.classList.toggle('off', Boolean(status.expiryMessage));
      return;
    }
    const missing = (status.featureStatus || [])
      .filter((f) => !f.allowed)
      .map((f) => f.name);
    // 功能名稱來自伺服器，前端不自行翻譯；缺的清單與句型則走目錄，
    // 否則切到英文介面時會出現「Not authorized: 影片 (缺 錄影、剪輯)」這種混雜畫面。
    const reason = missing.length
      ? `${status.message || status.decision} ${tx('state.missingFeatures', { features: missing.join(t('list.separator')) })}`
      : status.message || status.decision;
    el.textContent = t('state.unauthorized', { reason });
    el.classList.add('off');
  } catch (err) {
    if (el) el.textContent = '';
  }
}

async function refreshAll() {
  await Promise.allSettled([refreshChannels(), renderBoard(), refreshTimeline(), renderMap(), renderSmartwall(), renderPos(), renderDaily(), renderSchedules(), renderPatrols(), renderDoor(), renderDetections(), renderNotifs(), renderExports()]);
}

boot();