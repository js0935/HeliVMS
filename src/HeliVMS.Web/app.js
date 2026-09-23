import {
  accountRows,
  ackPayload,
  ackRate,
  auditRows,
  auditFilter,
  buildTimelineQuery,
  canAct,
  configCard,
  dailyCard,
  eventRow,
  focusLayout,
  formatTimestamp,
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
  gapLabel,
  gridLayout,
  parseLogin,
  parseWsMessage,
  pinStyles,
  posTotals,
  smartwallSnapshot,
  sortBoard,
  summarizeBoard,
  tileClass,
  tileLabel,
  triagePayload,
} from './lib.js';

const $ = (id) => document.getElementById(id);
const key = () => localStorage.getItem('helivms.apiKey') ?? $('apikey').value ?? '';

async function api(path, init = {}) {
  const headers = new Headers(init.headers);
  headers.set('Authorization', `Bearer ${key()}`);
  const response = await fetch(path, { ...init, headers });
  if (!response.ok) throw new Error(`${path} -> ${response.status}`);
  return response.json();
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
    .map((c) => `<li>${c.id} · ${c.name ?? c.location ?? ''}</li>`)
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
  sessionStorage.setItem('helivms.session', JSON.stringify(session ?? {}));
  updateChrome();
}

async function submitLogin(event) {
  event.preventDefault();
  const username = $('user').value.trim();
  const password = $('pass').value;
  $('pass').value = '';
  let payload = {};
  try {
    const res = await fetch('/api/accounts/authenticate', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${key()}` },
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
      await Promise.allSettled([renderAccounts(), renderConfig(), renderAudit(), renderEvidence(), renderBackup(), renderProviders(), renderHolds(), renderRules(), renderShares(), renderReds(), renderRep(), renderBoard()]);
    }
  }
  await refreshBoard();
}

function logout() {
  storeSession(null);
  window.location.reload();
}

function updateChrome() {
  const session = readSession();
  const who = $('who');
  who.textContent = session ? `${session.role} · ${session.name ?? ''}`.trim() : '未登入';
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
  $('alarm-panel').hidden = !admin;
  $('logout').hidden = !session;
}

async function renderAudit() {
  const all = auditRows((await api('/api/audit?limit=200')).items);
  const category = $('audit-category').value;
  const actor = $('audit-actor').value;
  const rows = auditFilter(all, category, actor);
  $('audit').querySelector('tbody').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>${formatTimestamp(r.occurredAtUtc)}</td><td>${r.category}</td><td>${r.actor}</td>` +
        `<td>${r.action}</td><td>${r.targetType ?? ''}${r.targetId ? `:${r.targetId}` : ''}</td>` +
        `<td title="${r.detail ?? ''}">${(r.detail ?? '').slice(0, 40)}</td></tr>`,
    )
    .join('');
  $('audit-csv').href = `/api/audit/export.csv?category=${encodeURIComponent(category)}&actor=${encodeURIComponent(actor)}`;
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
    ? `使用 ${usage.gb.toFixed(2)}GiB · 保留 ${usage.retentionDays} 天 · 警報 ${usage.alarmRetentionDays} 天 · 浮水印 ${usage.watermarkGb}GB`
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
    $('config-msg').textContent = '已儲存';
  } catch (err) {
    $('config-msg').textContent = String(err);
  }
}

async function runRetention() {
  try {
    const result = await api('/api/retention/run', { method: 'POST' });
    $('config-msg').textContent =
      `清理完成：錄影保留 ${result.agePurged} · 浮水印 ${result.watermarkPurged} · 警報 ${result.alarmPurged} · 釋放 ${(result.bytesFreed / 1073741824).toFixed(2)}GiB`;
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
        `<tr><td>${a.username}</td><td>${a.role}</td><td>${a.enabled ? '啟用' : '停用'}${a.locked ? ' · 鎖定' : ''}</td>` +
        `<td><button data-toggle="${a.id}" data-enable="${!a.enabled}">${a.enabled ? '停用' : '啟用'}</button>` +
        `<button data-role="${a.id}" data-next="${a.role === 'admin' ? 'viewer' : 'admin'}">轉${a.role === 'admin' ? 'v' : 'admin'}</button>` +
        `<button data-del="${a.id}">刪除</button></td></tr>`,
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

async function refreshBoard() {
  const rows = sortBoard(await api('/api/alarms/board?take=50'));
  const s = summarizeBoard(rows);
  $('board-summary').textContent = `共 ${s.total} · 嚴重 ${s.critical} · 逾期 ${s.overdue}`;
  $('kpi').textContent = `ack ${ackRate(rows)}% · 嚴重 ${s.critical}`;
  $('board').querySelector('tbody').innerHTML = rows
    .map(
      (r) =>
        `<tr class="${r.priority}"><td>${r.eventId}</td><td>${r.channelId}</td><td>${r.eventType ?? ''}</td><td>${r.priority}</td>` +
        `<td><button data-ack="${r.eventId}" class="act" ${r.status === 'acknowledged' ? 'disabled' : ''}>ack</button>` +
        `<button data-triage="${r.eventId}" data-p="critical" class="act">!!</button></td></tr>`,
    )
    .join('');

  document.querySelectorAll('button[data-ack]').forEach((btn) => {
    btn.addEventListener('click', () => actAck(Number(btn.dataset.ack)));
  });
  document.querySelectorAll('button[data-triage]').forEach((btn) => {
    btn.addEventListener('click', () => actTriage(Number(btn.dataset.triage), btn.dataset.p));
  });
}

async function actAck(id) {
  const payload = ackPayload(id, true);
  await api(`/api/events/${payload.id}/ack`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload.body),
  });
  await refreshBoard();
}

async function actTriage(id, priority) {
  const payload = triagePayload(id, priority);
  await api(`/api/events/${payload.id}/triage`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(payload.body),
  });
  await refreshBoard();
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
      const el = $('swgrid');
      el.innerHTML = '';
      const cells = snap.cells.length ? snap.cells : [{}];
      const pins = focusLayout(cells, 4);
      cells.forEach((c, i) => {
        const box = document.createElement('div');
        box.className = `swtile ${tileClass(c)}`;
        Object.assign(box.style, pinStyles(pins[i], 0.01));
        const t = tileLabel(c);
        box.title = `頻道 ${t.channel} · ${t.type} (${t.priority})`;
        box.textContent = String(t.channel);
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
      `錄影 ${card.hours}h · ${card.gb}GiB · 中斷 ${card.disconnects}`;
    $('daily-events').querySelector('tbody').innerHTML = card.events
      .map((e) => `<tr><td>${e.type}</td><td>${e.count}</td></tr>`)
      .join('');
  } catch (err) {
    $('daily-summary').textContent = String(err);
  }
}

async function renderSchedules() {
  const list = await api('/api/recording/schedules').catch(() => []);
  $('sched-body').innerHTML = list
    .map(
      (s) =>
        `<tr><td>${s.channelId}</td><td>${scheduleLabel(s)}</td><td>${s.enabled ? '啟用' : '停用'}</td>
         <td><button data-sched-del="${s.id}">刪除</button></td></tr>`,
    )
    .join('');
  $('sched-count').textContent = `（${list.length}）`;
  $('sched-body').querySelectorAll('button[data-sched-del]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      const ok = await api(`/api/recording/schedules/${btn.dataset.schedDel}`, { method: 'DELETE' }).catch(() => null);
      if (ok) renderSchedules();
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
    const ok = await fetch('/api/recording/schedules', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).catch(() => null);
    if (ok?.ok) renderSchedules();
  });
}

async function renderPatrols() {
  const list = await api('/api/patrols').catch(() => []);
  $('patrol-body').innerHTML = list
    .map(
      (p) =>
        `<tr><td>${patrolLabel(p)}</td><td>${p.enabled ? '啟用' : '停用'}</td>
         <td><button data-patrol-del="${p.id}">刪除</button></td></tr>`,
    )
    .join('');
  $('patrol-count').textContent = `（${list.length}）`;
  $('patrol-body').querySelectorAll('button[data-patrol-del]').forEach((btn) =>
    btn.addEventListener('click', async () => {
      const ok = await api(`/api/patrols/${btn.dataset.patrolDel}`, { method: 'DELETE' }).catch(() => null);
      if (ok) renderPatrols();
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
    const ok = await fetch('/api/patrols', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).catch(() => null);
    if (ok?.ok) renderPatrols();
  });
}

async function renderEvidence() {
  const rows = evRows(await api('/api/evidence').catch(() => []));
  $('evidence-body').innerHTML = rows
    .map(
      (m) =>
        `<tr><td>#${m.id}</td><td>${m.status}</td><td>${m.createdAt?.slice(0, 19) ?? ''}</td><td>${m.items}</td></tr>`,
    )
    .join('');
  $('evidence-count').textContent = `（${rows.length}）`;
}

async function renderBackup() {
  const rows = backupRows(await api('/api/backup/runs').catch(() => []));
  $('backup-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${r.seq}</td><td>${r.runAt?.slice(0, 19) ?? ''}</td><td>${r.copied}</td><td>${r.bytes}</td><td>${r.failed}</td><td>${r.advanced ? '已推進' : '未推進'}</td><td class="muted">${r.target}</td></tr>`,
    )
    .join('');
  $('backup-count').textContent = `（${rows.length}）`;
}

function bindBackupForm() {
  const form = $('backup-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const src = form.querySelector('input[name="bsrc"]').value.trim();
    const dst = form.querySelector('input[name="bdst"]').value.trim();
    if (!src || !dst) return;
    const resp = await fetch('/api/backup/run', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ sourceRoot: src, targetRoot: dst }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('backup-msg').textContent = resp.ok
      ? `完成：掃描 ${body.scanned} → 複製 ${body.copied}（${(body.copiedBytes / 1048576).toFixed(1)}MiB），失敗 ${body.failed}`
      : body?.error ?? '失敗';
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
        `<tr><td>${e.time}</td><td>${e.device}</td><td>${e.door}</td><td>${e.card}</td><td>${e.direction}</td><td class="${e.granted ? 'ok' : 'bad'}">${e.granted ? '放行' : '拒絕'}</td><td class="muted">${e.reason}</td></tr>`,
    )
    .join('');
  $('door-count').textContent = `（${rows.length}）`;
}

async function renderDetections() {
  const conf = (document.getElementById('det-conf')?.value ?? '').trim();
  const cls = (document.getElementById('det-class')?.value ?? '').trim();
  const q = new URLSearchParams();
  if (conf && Number(conf) > 0) q.set('minConfidence', conf);
  if (cls) q.set('class', cls);
  const rows = detRows(await api(`/api/detections?${q}`).catch(() => []));
  $('det-body').innerHTML = rows
    .map(
      (d) =>
        `<tr><td>${d.time}</td><td>ch${d.channel}</td><td>${d.cls}</td><td>${(d.conf * 100).toFixed(0)}%</td><td>${d.x.toFixed(2)},${d.y.toFixed(2)}</td><td>${d.box}</td></tr>`,
    )
    .join('');
  $('det-count').textContent = `（${rows.length}）`;
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
        `<tr><td>${n.time}</td><td>ch${n.channel}</td><td>${n.event}</td><td>${n.route}</td><td class="${n.ok ? 'ok' : 'bad'}">${n.ok ? '成功' : '失敗'}</td><td>${n.attempts}</td><td class="muted">${n.detail}</td></tr>`,
    )
    .join('');
  $('notif-count').textContent = `（${rows.length}）`;
}

async function renderExports() {
  const rows = exportRows(await api('/api/exports').catch(() => []));
  $('export-body').innerHTML = rows
    .map(
      (j) =>
        `<tr><td>#${j.id}</td><td>ch${j.channel}</td><td>${j.stream}</td><td>${j.start}</td><td>${j.end}</td><td>${j.status}</td><td>${j.file ? `${j.file}` : ''}</td><td class="muted">${j.sha}${j.error ? ` · ${j.error}` : ''}</td></tr>`,
    )
    .join('');
  $('export-count').textContent = `（${rows.length}）`;
}

function bindExportForm() {
  const form = $('export-form');
  form.addEventListener('submit', async (ev) => {
    ev.preventDefault();
    const ch = Number(form.querySelector('input[name="ech"]').value);
    const from = form.querySelector('input[name="efrom"]').value;
    const to = form.querySelector('input[name="eto"]').value;
    if (!ch || !from || !to) return;
    const resp = await fetch('/api/exports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ channelId: ch, stream: 'main', fromUtc: new Date(from).toISOString(), toUtc: new Date(to).toISOString() }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('export-msg').textContent = resp.ok ? `已排入 #${body.id}` : body?.error ?? '失敗';
    if (resp.ok) renderExports();
  });
}

async function renderProviders() {
  const rows = providerRows(await api('/api/auth/providers').catch(() => []));
  $('provider-body').innerHTML = rows
    .map(
      (p) =>
        `<tr><td>#${p.id}</td><td>${p.name}</td><td>${p.kind}</td><td><input type="checkbox" data-provider-toggle="${p.id}" ${p.enabled ? 'checked' : ''}></td><td><button class="danger" data-provider-del="${p.id}">刪除</button></td><td class="muted">${p.config.slice(0, 40)}</td></tr>`,
    )
    .join('');
  $('provider-count').textContent = `（${rows.length}）`;
  Array.from(document.querySelectorAll('[data-provider-toggle]')).forEach((cb) => {
    cb.addEventListener('change', async () => {
      await fetch(`/api/auth/providers/${cb.dataset.providerToggle}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: cb.checked }),
      });
      renderProviders();
    });
  });
  Array.from(document.querySelectorAll('[data-provider-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await fetch(`/api/auth/providers/${btn.dataset.providerDel}`, { method: 'DELETE' });
      renderProviders();
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
    const resp = await fetch('/api/auth/providers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ name, kind, configJson: cfg, enabled: true }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('provider-msg').textContent = resp.ok ? `已新增 #${body.id}` : body?.error ?? '失敗';
    if (resp.ok) renderProviders();
  });
}

async function renderHolds() {
  const rows = holdRows(await api('/api/legal-holds').catch(() => []));
  $('hold-body').innerHTML = rows
    .map(
      (h) =>
        `<tr><td>#${h.id}</td><td>ch${h.channel}</td><td>${h.from}</td><td>${h.to}</td><td>${h.reason}</td><td>${h.by}</td><td class="${h.active ? 'ok' : 'bad'}">${h.active ? '生效' : '已撤銷'}</td><td class="muted">${h.revokedBy}</td><td>${h.active ? `<button class="danger" data-hold-revoke="${h.id}">撤銷</button>` : ''}</td></tr>`,
    )
    .join('');
  $('hold-count').textContent = `（${rows.length}）`;
  Array.from(document.querySelectorAll('[data-hold-revoke]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const reason = prompt('撤銷原因');
      if (reason === null) return;
      await fetch(`/api/legal-holds/${btn.dataset.holdRevoke}/revoke`, {
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
    const resp = await fetch('/api/legal-holds', {
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
    $('hold-msg').textContent = resp.ok ? `已建立 #${body.id}` : body?.error ?? '失敗';
    if (resp.ok) renderHolds();
  });
}

async function renderRules() {
  const rows = ruleRows(await api('/api/alert-rules').catch(() => []));
  $('rule-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${r.id}</td><td>${r.name}</td><td>${r.event || '—'}</td><td>${r.channel || '—'}</td><td>${r.keyword || '—'}</td><td>${r.channels || '—'}</td><td><input type="checkbox" data-rule-toggle="${r.id}" ${r.enabled ? 'checked' : ''}></td><td>${r.min}</td><td><button class="danger" data-rule-del="${r.id}">刪除</button></td></tr>`,
    )
    .join('');
  $('rule-count').textContent = `（${rows.length}）`;
  Array.from(document.querySelectorAll('[data-rule-toggle]')).forEach((cb) => {
    cb.addEventListener('change', async () => {
      await fetch(`/api/alert-rules/${cb.dataset.ruleToggle}/enabled`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ enabled: cb.checked }),
      });
      renderRules();
    });
  });
  Array.from(document.querySelectorAll('[data-rule-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await fetch(`/api/alert-rules/${btn.dataset.ruleDel}`, { method: 'DELETE' });
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
    const resp = await fetch('/api/alert-rules', {
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
    $('rule-msg').textContent = resp.ok ? `已新增 #${body.id}` : body?.error ?? '失敗';
    if (resp.ok) renderRules();
  });
}

async function renderShares() {
  const rows = shareRows(await api('/api/shares').catch(() => []));
  $('share-body').innerHTML = rows
    .map(
      (s) =>
        `<tr><td>#${s.id}</td><td>${s.kind}</td><td>${s.label || '—'}</td><td class="muted">${s.path}</td><td>${s.token}…</td><td>${s.uses}</td><td class="${s.active ? 'ok' : 'bad'}">${s.active ? '有效' : '失效'}</td><td>${s.active ? `<button class="danger" data-share-revoke="${s.id}">撤銷</button>` : ''}</td></tr>`,
    )
    .join('');
  $('share-count').textContent = `（${rows.length}）`;
  Array.from(document.querySelectorAll('[data-share-revoke]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await fetch(`/api/shares/${btn.dataset.shareRevoke}/revoke`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: '{}',
      });
      renderShares();
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
    const resp = await fetch('/api/shares', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ kind, resourcePath: path, label: label || null, maxUses }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('share-msg').textContent = resp.ok ? `已建立 ${body.token.slice(0, 12)}…` : body?.error ?? '失敗';
    if (resp.ok) renderShares();
  });
}

async function renderReds() {
  const rows = redRows(await api('/api/redactions?limit=200').catch(() => []));
  $('red-body').innerHTML = rows
    .map(
      (r) =>
        `<tr><td>#${r.id}</td><td>${r.source}</td><td>${r.ref}</td><td>CH${r.channel}</td><td>${r.time}</td><td>${r.rect}</td><td class="${r.filled ? 'ok' : ''}">${r.filled ? '塗滿' : '框選'}</td><td><button class="danger" data-red-del="${r.id}">移除</button></td></tr>`,
    )
    .join('');
  $('red-count').textContent = `（${rows.length}）`;
  Array.from(document.querySelectorAll('[data-red-del]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      await fetch(`/api/redactions/${btn.dataset.redDel}`, { method: 'DELETE' });
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
    const resp = await fetch('/api/redactions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(payload),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('red-msg').textContent = resp.ok ? `已新增 #${body.id}` : body?.error ?? '失敗';
    if (resp.ok) renderReds();
  });
}

async function renderRep() {
  const rows = repRows(await api('/api/replication').catch(() => []));
  $('rep-body').innerHTML = rows
    .map(
      (j) =>
        `<tr><td>#${j.id}</td><td class="muted">${j.src}</td><td class="muted">${j.dst}</td><td>${j.minutes} 分</td><td class="${j.enabled ? 'ok' : ''}">${j.enabled ? '啟用' : '停用'}</td><td class="${j.fails > 0 ? 'bad' : 'ok'}">${j.lastResult || '—'}${j.fails > 0 ? `（連續 ${j.fails} 次）` : ''}</td><td class="${j.due ? 'bad' : ''}">${j.due ? '到期' : '—'}</td></tr>`,
    )
    .join('');
  $('rep-count').textContent = `（${rows.length}）`;
}

async function renderBoard() {
  const [board, summary] = await Promise.all([
    api('/api/alarm-board?take=200').catch(() => []),
    api('/api/alarm-board/summary').catch(() => null),
  ]);
  const rows = alarmRows(board);
  $('alarm-body').innerHTML = rows
    .map(
      (b) =>
        `<tr class="${b.overdue ? 'overdue' : ''}"><td>#${b.id}</td><td>CH${b.channel}</td><td>${b.event}</td><td>${b.start}</td><td>${b.priority}<td>${b.status}</td><td class="${b.overdue ? 'bad' : ''}">${b.overdue ? '逾期' : '—'}</td><td><select data-pri="${b.id}"><option value="low" ${b.priority === 'low' ? 'selected' : ''}>低</option><option value="normal" ${b.priority === 'normal' ? 'selected' : ''}>一般</option><option value="high" ${b.priority === 'high' ? 'selected' : ''}>高</option><option value="critical" ${b.priority === 'critical' ? 'selected' : ''}>緊急</option></select><button data-triage="${b.id}">分診</button><button data-ack="${b.id}" class="${b.status === 'acknowledged' ? 'ok' : ''}">確認</button><button class="danger" data-fa="${b.id}">誤報</button></td></tr>`,
    )
    .join('');
  const chips = [
    ['未處理', summary?.Pending ?? 0],
    ['已確認', summary?.Acknowledged ?? 0],
    ['已處置', summary?.Actioned ?? 0],
    ['誤報', summary?.FalseAlarm ?? 0],
    ['逾期', summary?.Overdue ?? 0],
  ];
  $('alarm-count').textContent = `（${rows.length}）`;
  $('alarm-badges').innerHTML = chips.map(([label, n]) => `<span class="chip">${label}：${n}</span>`).join('');
  bindBoardActions();
}

function bindBoardActions() {
  Array.from(document.querySelectorAll('[data-triage]')).forEach((btn) => {
    btn.addEventListener('click', async () => {
      const id = btn.dataset.triage;
      const priority = document.querySelector(`[data-pri="${id}"]`).value;
      await fetch(`/api/alarm-board/${id}/triage`, {
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
      await fetch(`/api/alarm-board/${id}/ack`, {
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
      await fetch(`/api/alarm-board/${id}/disposition`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ status: 'false_alarm', assignedTo: null, note: '管理面板標記誤報' }),
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
    const resp = await fetch('/api/evidence/package', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ bundleName: name, files }),
    }).catch(() => null);
    if (!resp) return;
    const body = await resp.json().catch(() => null);
    $('evidence-msg').textContent = resp.ok
      ? `已打包：${body.bundlePath} · sha256 ${body.bundleSha256?.slice(0, 12)}…`
      : body?.error ?? '失敗';
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

async function searchEvents(q) {
  const to = new Date();
  const from = new Date(to.getTime() - 24 * 3600 * 1000);
  const params = new URLSearchParams({ q, from: from.toISOString(), to: to.toISOString(), limit: '50' });
  const page = await api(`/api/events/search?${params}`);
  $('events').querySelector('tbody').innerHTML = page.items
    .map((e) => {
      const row = eventRow(e);
      return `<tr><td>${row.id}</td><td>${row.time}</td><td>${row.channel}</td><td>${row.type}</td><td>${row.status ?? ''}</td></tr>`;
    })
    .join('');
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
    div.style.left = `${bar.leftFraction * 100}%`;
    div.style.width = `${Math.max(bar.widthFraction * 100, 0.2)}%`;
    el.appendChild(div);
  }
  for (const marker of timeline.markers) {
    const div = document.createElement('div');
    div.className = 'marker';
    div.style.left = `${marker.xFraction * 100}%`;
    el.appendChild(div);
  }
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
  const saved = localStorage.getItem('helivms.apiKey');
  if (saved) $('apikey').value = saved;
  $('apikey').addEventListener('change', () => {
    localStorage.setItem('helivms.apiKey', $('apikey').value);
    location.reload();
  });
  $('audit-category').addEventListener('change', renderAudit);
  $('audit-actor').addEventListener('input', renderAudit);
  $('account-form').addEventListener('submit', submitAccount);
  $('config-form').addEventListener('submit', saveConfig);
  $('retention-run').addEventListener('click', runRetention);
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
  ['door-card', 'door-ok', 'door-denied'].forEach((id) => {
    const el = $(id);
    if (el) el.addEventListener(el.type === 'checkbox' ? 'change' : 'input', renderDoor);
  });
  ['det-conf', 'det-class'].forEach((id) => {
    const el = $(id);
    if (el) el.addEventListener('input', renderDetections);
  });
}

async function boot() {
  wire();
  updateChrome();
  await refreshHealth();
  await Promise.allSettled([refreshChannels(), refreshBoard(), refreshTimeline(), renderMap(), renderSmartwall(), renderPos(), renderDaily(), renderSchedules(), renderPatrols(), renderDoor(), renderDetections(), renderNotifs(), renderExports()]);
  bindScheduleForm();
  bindPatrolForm();
  connectLive();
}

boot();