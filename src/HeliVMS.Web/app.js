import {
  buildTimelineQuery,
  eventRow,
  formatTimestamp,
  gapLabel,
  parseWsMessage,
  sortBoard,
  summarizeBoard,
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

async function refreshBoard() {
  const rows = sortBoard(await api('/api/alarms/board?take=50'));
  const s = summarizeBoard(rows);
  $('board-summary').textContent = `共 ${s.total} · 嚴重 ${s.critical} · 逾期 ${s.overdue}`;
  $('board').querySelector('tbody').innerHTML = rows
    .map(
      (r) =>
        `<tr class="${r.priority}"><td>${r.eventId}</td><td>${r.channelId}</td><td>${r.eventType ?? ''}</td><td>${r.priority}</td></tr>`,
    )
    .join('');
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

async function refreshTimeline() {
  const day = new Date();
  day.setUTCHours(0, 0, 0, 0);
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
  };
  socket.onclose = () => {
    $('live').textContent = 'WS off';
    $('live').classList.remove('on');
  };
}

function wire() {
  const saved = localStorage.getItem('helivms.apiKey');
  if (saved) $('apikey').value = saved;
  $('apikey').addEventListener('change', () => {
    localStorage.setItem('helivms.apiKey', $('apikey').value);
    location.reload();
  });
  $('search-form').addEventListener('submit', (e) => {
    e.preventDefault();
    searchEvents($('q').value || '*').catch(console.error);
  });
}

async function boot() {
  wire();
  await refreshHealth();
  await Promise.allSettled([refreshChannels(), refreshBoard(), refreshTimeline()]);
  connectLive();
}

boot();