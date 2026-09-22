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
  formatTimestamp,
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
      await Promise.allSettled([renderAccounts(), renderConfig(), renderAudit()]);
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
      }),
    });
    await renderConfig();
    $('config-msg').textContent = '已儲存';
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
      const pins = gridLayout(cells.length, 4);
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
  $('search-form').addEventListener('submit', (e) => {
    e.preventDefault();
    searchEvents($('q').value || '*').catch(console.error);
  });
}

async function boot() {
  wire();
  updateChrome();
  await refreshHealth();
  await Promise.allSettled([refreshChannels(), refreshBoard(), refreshTimeline(), renderMap(), renderSmartwall(), renderPos(), renderDaily()]);
  connectLive();
}

boot();