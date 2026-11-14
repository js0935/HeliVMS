import { applyDom, DEFAULT_LOCALE, locale, setLocale, t } from './i18n.js';
import { createAlertSocket } from './alerts.js';
import { createLiveViewer } from './live.js';
import {
  bearerHeader,
  buildCentralPlaylistQuery,
  buildPlaylistQuery,
  buildRecordingClock,
  centralChannelRows,
  configCard,
  esc,
  eventRow,
  formatLocalClock,
  formatTimestamp,
  mseMimeType,
  parseFmp4InitTracks,
  parseFmp4VideoSpan,
  parseLogin,
  parseM3u8,
  parseWsMessage,
  pollGate,
  tx,
} from './lib.js';

const $ = (id) => document.getElementById(id);
const key = () => (localStorage.getItem('helivms.apiKey') || $('apikey')?.value || '').trim();
const POLL_MS = 12000; // 授權與健康狀態的輪詢；分頁隱藏或焦點在控制項上時暫停。

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

/** 連線旗標：任何請求成功即可正常；網路層失敗時在頂列顯示「連線中斷」。（HTTP 錯誤狀態不算斷線） */
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

async function refreshHealth() {
  try {
    const h = await api('/api/health');
    $('health').textContent = `ok · ${h.channels} ch · db ${h.database}`;
    $('health').classList.add('on');
  } catch (err) {
    $('health').textContent = String(err);
  }
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
    // M306：登入成功即拿到工作階段 token；存成 apiRaw 的憑證，之後不必再手動餵 API 金鑰。
    if (state.token) {
      localStorage.setItem('helivms.apiKey', state.token);
      $('apikey').value = state.token;
    }
  }
  await refreshAll();
}

async function logout() {
  // 撤銷伺服器端工作階段（M306）：token 一撤銷，帶著它的請求都回 401。
  // best-effort——連不上時仍要清掉本機憑證，伺服器端最終也會因過期而失效。
  try {
    await apiRaw('/api/accounts/logout', { method: 'POST' });
  } catch {
    // 忽略：頁面仍要重載並清除本機狀態。
  }
  storeSession(null);
  localStorage.removeItem('helivms.apiKey');
  $('apikey').value = '';
  stopLiveView();
  stopSnapshotView();
  window.location.reload();
}

function updateChrome() {
  const session = readSession();
  const who = $('who');
  who.textContent = session ? `${session.role} · ${session.name ?? ''}`.trim() : t('state.notSignedIn');
  who.classList.toggle('on', Boolean(session));
  $('logout').hidden = !session;
}

/** 功能分頁。切換只改可見性與 ARIA 狀態，不重建內容，所以播放中的畫面不會被打斷。 */
function showTab(name) {
  for (const btn of document.querySelectorAll('[data-tab]')) {
    const on = btn.dataset.tab === name;
    btn.classList.toggle('active', on);
    btn.setAttribute('aria-selected', String(on));
  }
  for (const panel of document.querySelectorAll('[data-panel]')) {
    panel.hidden = panel.dataset.panel !== name;
  }
  // 設定值不隨輪詢重載：每 12 秒覆寫會蓋掉正在編輯的欄位，所以只在開啟設定分頁時載入。
  if (name === 'settings') void loadSettings();
  // 裝置資訊的 /config/usage 會掃錄影目錄（ReconcileUsage），只在開啟分頁時載入。
  if (name === 'info') void refreshDeviceInfo();
  if (name === 'export') {
    refreshExportSources();
    void refreshExports();
  }
}

function bindTabs() {
  for (const btn of document.querySelectorAll('[data-tab]')) {
    btn.addEventListener('click', () => showTab(btn.dataset.tab));
  }
}

const snapshotViewer = createLiveViewer({ apiRaw, t });

/** 目前格線上的每一格各自一條 WHEP 會話；「停止」一次整批收掉。 */
let liveCells = [];

/** 版面下拉的值就是格數（1／4／9／16／25），開根號即為每列格數。 */
function liveLayoutCount() {
  const value = Number($('live-layout')?.value);
  return [1, 4, 9, 16, 25].includes(value) ? value : 1;
}

/** 多選清單目前勾選的頻道（依清單順序）。 */
function selectedChannelIds() {
  const select = $('live-channels');
  if (!select) return [];
  return [...select.selectedOptions]
    .map((o) => Number(o.value))
    .filter((n) => Number.isInteger(n) && n > 0);
}

function channelName(channelId) {
  return localChannels.find((c) => Number(c.id) === channelId)?.name || `#${channelId}`;
}

/**
 * 依版面把格線重畫成 N 格空畫面。每格都有自己的 <video> 與角標，開檔時再逐格
 * 建立 WHEP 會話——切版面時不必保留舊連線，直接重畫最單純。
 */
function renderLiveGrid() {
  const grid = $('live-grid');
  if (!grid) return;
  const count = liveLayoutCount();
  const cols = Math.round(Math.sqrt(count));
  grid.style.gridTemplateColumns = `repeat(${cols}, minmax(0, 1fr))`;
  grid.innerHTML = Array.from({ length: count }, () =>
    '<div class="live-cell"><video class="player" autoplay muted playsinline></video><span class="live-cell-label"></span></div>',
  ).join('');
}

/**
 * 多分割即時監看：版面決定格數，多選清單決定要看哪幾路；每一格各自一條 WHEP 會話。
 *
 * 選超過格數時只取前 N 路（不報錯，因為使用者通常只是懶得改清單）。
 * 刻意平行開檔：ICE 收集最長 3 秒，逐格等待會讓 5x5 等到天荒地老。
 */
async function openLiveView(event) {
  event.preventDefault();
  const msg = $('live-msg');
  const channels = selectedChannelIds();

  await stopLiveView();
  renderLiveGrid();

  if (channels.length === 0) {
    msg.textContent = t('live.pickChannel');
    return;
  }

  const cells = [...$('live-grid').querySelectorAll('.live-cell')];
  const shown = channels.slice(0, liveLayoutCount());

  msg.textContent = t('state.liveConnecting');

  liveCells = shown.map((channelId, i) => {
    const cell = cells[i];
    const video = cell.querySelector('video');
    const label = cell.querySelector('.live-cell-label');
    label.textContent = channelName(channelId);
    return { channelId, video, label, viewer: createLiveViewer({ apiRaw, t }) };
  });

  const results = await Promise.all(
    liveCells.map(({ viewer, channelId, video, label }) =>
      viewer.open(channelId, video, (text) => {
        label.textContent = `${channelName(channelId)} · ${text}`;
      }),
    ),
  );

  const ok = results.filter(Boolean).length;
  msg.textContent = ok === liveCells.length && ok > 0
    ? t('state.liveReady')
    : t('live.someFailed', { ok, total: liveCells.length });
  $('live-meta').textContent = t('live.gridMeta', {
    count: ok,
    channels: shown.map(channelName).join(t('list.separator')),
  });
}

async function stopLiveView() {
  if (liveCells.length === 0) return;
  const cells = liveCells;
  liveCells = [];
  const results = await Promise.all(cells.map(({ viewer }) => viewer.stop()));
  if (results.some(Boolean)) {
    $('live-msg').textContent = t('state.liveStopped');
    $('live-meta').textContent = '';
  }
}

/**
 * 截圖：把即時畫面畫進 canvas 再下載 PNG。
 *
 * 刻意不做伺服器端截圖端點——畫面上已經有解碼完成的影像，前端複製一份是零延遲、
 * 零後端相依，而且抓到的就正是操作員當下看到的那一格。
 */
async function openSnapshotView(event) {
  event.preventDefault();
  const channelId = Number($('snap-channel').value);
  const started = await snapshotViewer.open(channelId, $('snap-video'), (text) => {
    $('snap-msg').textContent = text;
  });
  $('snap-meta').textContent = started
    ? t('live.viewerCount', { channel: channelId, viewers: 1 })
    : '';
}

async function stopSnapshotView() {
  const stopped = await snapshotViewer.stop();
  if (stopped) {
    $('snap-msg').textContent = t('state.liveStopped');
    $('snap-meta').textContent = '';
  }
}

function captureSnapshot() {
  const video = $('snap-video');
  const msg = $('snap-msg');
  // 沒有尺寸代表根本沒有解碼中的畫面（還沒開、或 WebRTC 失敗）；硬畫只會得到黑圖。
  if (!video.videoWidth || !video.videoHeight) {
    msg.textContent = t('state.snapshotFailed');
    return;
  }
  const canvas = document.createElement('canvas');
  canvas.width = video.videoWidth;
  canvas.height = video.videoHeight;
  canvas.getContext('2d').drawImage(video, 0, 0);
  canvas.toBlob((blob) => {
    if (!blob) {
      msg.textContent = t('state.snapshotFailed');
      return;
    }
    const url = URL.createObjectURL(blob);
    const link = document.createElement('a');
    link.href = url;
    link.download = `snapshot-ch${Number($('snap-channel').value) || 0}-${Date.now()}.png`;
    link.click();
    URL.revokeObjectURL(url);
    msg.textContent = t('state.snapshotDone');
  }, 'image/png');
}

let playChannels = [];

/**
 * 回放來源選單：本機 + 每個已註冊的子節點（M301／M302）。
 * 選子節點時 playRemote 改打跨機代理，體驗與本機相同。單台離線時後端已跳過，這裡最多整份為空。
 */
async function refreshPlaySources() {
  const nodes = await api('/api/central/nodes').catch(() => []);
  const sel = $('play-node');
  if (sel) {
    const current = sel.value;
    sel.innerHTML = [`<option value="">${esc(t('replay.nodeLocal'))}</option>`]
      .concat(nodes.map((n) => `<option value="${Number(n.id)}">${esc(n.name)}</option>`))
      .join('');
    sel.value = current;
  }
  playChannels = centralChannelRows(await api('/api/central/channels').catch(() => []));
  fillPlayChannels();
}

/** 選了子節點就只列該節點的頻道，避免使用者猜編號；本機則列資料庫裡的實機頻道（91/93/94 這種主鍵）。 */
function fillPlayChannels() {
  const list = $('play-channel-options');
  if (!list) return;
  const nodeValue = $('play-node')?.value ?? '';
  const rows = nodeValue
    ? playChannels
      .filter((c) => c.nodeId === Number(nodeValue))
      .map((c) => ({ value: c.channelId, label: c.channelName }))
    : localChannels.map((c) => ({ value: c.id, label: c.name }));
  list.innerHTML = rows
    .map((r) => `<option value="${Number(r.value)}">${esc(r.label)}</option>`)
    .join('');

  // 本機時預設第一路可用頻道。原本輸入框硬寫 value="1"，但實機主鍵通常不是 1，
  // 使用者一按「載入回放」就得到「此時段沒有錄影」，誤以為回放功能壞了。
  const input = $('play-channel');
  if (input && !nodeValue && !input.value && rows.length > 0) {
    input.value = String(Number(rows[0].value));
  }
}

let localChannels = [];

/**
 * 本機頻道快選：串流端要的是資料庫主鍵（實機上是 91/93/94，不是從 1 起算），
 * 不給清單的話即時／截圖／PTZ 只能靠猜編號，猜錯伺服器只回「找不到這一路攝影機」。
 */
async function refreshLocalChannels() {
  const rows = await api('/api/channels').catch(() => []);
  localChannels = (Array.isArray(rows) ? rows : []).filter((c) => c.enabled !== false);

  // 回放頁的本機頻道建議也來自同一份清單。refreshAll 平行跑 refreshPlaySources，
  // 那支可能先於這裡執行、拿不到 localChannels，所以在這裡補一次。
  fillPlayChannels();

  // 即時監看是多選：重畫選項時要保留已勾選的頻道，否則每 12 秒的輪詢會把選取清掉。
  const select = $('live-channels');
  if (select) {
    const hadOptions = select.options.length > 0;
    const chosen = new Set([...select.selectedOptions].map((o) => o.value));
    select.innerHTML = localChannels
      .map((c) => `<option value="${Number(c.id)}">${esc(c.name)}</option>`)
      .join('');
    for (const option of select.options) option.selected = chosen.has(option.value);
    // 只有第一次載入、且使用者尚未選任何頻道時才預選第一路，讓「開啟」有預設可看。
    if (!hadOptions && select.options.length > 0) select.options[0].selected = true;
  }

  // 截圖／PTZ／IO 仍是單一數字欄位，用 datalist 提供建議。
  for (const id of ['snap-channel-options', 'ptz-channel-options', 'io-channel-options']) {
    const options = $(id);
    if (!options) continue;
    options.innerHTML = localChannels
      .map((c) => `<option value="${Number(c.id)}">${esc(c.name)}</option>`)
      .join('');
  }

  // 匯出頁的頻道下拉用同一份清單（保留已選擇的頻道，輪詢不會把選取洗掉）。
  refreshExportSources();
  const first = localChannels[0];
  if (!first) return;
  for (const id of ['snap-channel', 'ptz-channel', 'io-channel']) {
    const input = $(id);
    if (input && !input.value) input.value = String(Number(first.id));
  }
}

/**
 * 遠程回放（M239，§14.3 串流 P0）：HLS VOD → MediaSource。
 * 伺服器每個錄影檔自帶 ftyp＋moov，所以初始化段只送第一段的那一份，
 * 之後把每段的 moof/mdat 依序 append 就能在瀏覽器裡播。
 *
 * 每個錄影檔是獨立的 ffmpeg 行程寫的，時間戳都從 0 重新開始；不設 timestampOffset
 * 直接 append，後段的幀會覆蓋到前段的時間軸上，實際只剩第一段能播。所以要以
 * parseFmp4VideoSpan 量出的真實媒體長度累加偏移，把後段接到前段之後。
 * 回傳每段在合併時間軸上的位置與錄影牆鐘，讓時鐘能把 currentTime 換算成
 * 「這一刻是什麼時候錄的」。
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
  const timeline = [];
  try {
    await new Promise((resolve) => media.addEventListener('sourceopen', resolve, { once: true }));
    const buffer = media.addSourceBuffer(type);
    const fetchBytes = async (uri) => {
      const response = await apiRaw(uri);
      if (!response.ok) throw new Error(`${uri} -> ${response.status}`);
      return response.arrayBuffer();
    };
    const append = async (bytes, offsetSeconds) => {
      await new Promise((resolve, reject) => {
        buffer.addEventListener('updateend', resolve, { once: true });
        buffer.addEventListener('error', reject, { once: true });
        if (offsetSeconds !== null) buffer.timestampOffset = offsetSeconds;
        buffer.appendBuffer(bytes);
      });
    };

    const initBytes = playlist.initUri ? await fetchBytes(playlist.initUri) : null;
    if (initBytes) await append(initBytes, null);
    const tracks = parseFmp4InitTracks(initBytes);

    let mediaEnd = 0;
    for (const segment of playlist.segments) {
      const bytes = await fetchBytes(segment.uri);
      // 解析不出跨度時退回「假設從零開始＋牆鐘長度」：仍比直接覆蓋成只剩第一段好，
      // 時鐘誤差就是 EXTINF 與媒體長度的差（每段約一到兩秒）。
      const span = parseFmp4VideoSpan(tracks, bytes);
      const offset = mediaEnd - (span ? span.start : 0);
      const mediaStart = mediaEnd;
      const mediaDuration = span
        ? span.end - span.start
        : (Number.isFinite(segment.duration) && segment.duration > 0 ? segment.duration : 0);
      await append(bytes, offset);
      timeline.push({
        mediaStart,
        mediaEnd: mediaStart + mediaDuration,
        wallStartMs: Number.isFinite(segment.start) ? segment.start : null,
        wallDurS: Number.isFinite(segment.duration) ? segment.duration : null,
      });
      mediaEnd += mediaDuration;
    }
    media.endOfStream();
    return timeline;
  } finally {
    URL.revokeObjectURL(video.src);
  }
}

let playInFlight = false;
let playClock = { at: () => null };
let playClockText = '';

/**
 * 回放時鐘：把游標位置換算成錄影牆鐘（操作員要靠它確認畫面是什麼時候錄的）。
 * 只在字串真的改變時才寫 DOM——timeupdate 每秒會來好幾次，但寫入成本集中在
 * 「數字沒變」的那些次，與播放中的影片零爭用。
 */
function renderPlayClock() {
  const el = $('play-clock');
  if (!el) return;
  const at = playClock.at(Number($('play-video')?.currentTime));
  const text = at === null ? '' : t('replay.recordingTime', { time: formatLocalClock(at) });
  if (text === playClockText) return;
  playClockText = text;
  el.textContent = text;
}

function clearPlayClock() {
  playClock = { at: () => null };
  playClockText = '';
  const el = $('play-clock');
  if (el) el.textContent = '';
}

async function playRemote(event) {
  event.preventDefault();
  if (playInFlight) return;
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

  playInFlight = true;
  msg.textContent = t('state.loading');
  clearPlayClock();
  try {
    const nodeId = $('play-node')?.value ?? '';
    const response = await apiRaw(
      nodeId
        ? buildCentralPlaylistQuery({ nodeId, channelId, stream: 'main', from, to })
        : buildPlaylistQuery({ channelId, stream: 'main', from, to }),
    );
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

    const timeline = await attachPlaylist($('play-video'), playlist);
    playClock = buildRecordingClock(timeline);
    renderPlayClock();
    const first = playlist.segments[0];
    const last = playlist.segments[playlist.segments.length - 1];
    // 顯示的是「這批影片涵蓋哪一段錄影時間」，不是「影片長度幾秒」——
    // 操作員要回答的是「這是不是我要的時段」。
    $('play-meta').textContent = Number.isFinite(first?.start) && Number.isFinite(last?.start)
      ? t('replay.playlistWindow', {
        start: formatLocalClock(first.start),
        end: formatLocalClock(last.start + (Number.isFinite(last.duration) ? last.duration : 0) * 1000),
        segments: playlist.segments.length,
      })
      : t('replay.playlistMeta', { segments: playlist.segments.length, seconds: Math.round(playlist.duration) });
    msg.textContent = t('state.ready');
  } catch (err) {
    msg.textContent = err.message;
  } finally {
    playInFlight = false;
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

let ptzProfileToken = '';

/**
 * PTZ（M310 後端 /api/ptz）：列出設備碼流，之後的移動／預設點都靠 profileToken 定位。
 * 409 代表「這個設備不支援 PTZ」——與 404「頻道不存在」的意義完全不同，所以分開講。
 */
async function loadPtz(event) {
  event.preventDefault();
  const channelId = Number($('ptz-channel').value);
  const msg = $('ptz-msg');
  if (!Number.isFinite(channelId) || channelId <= 0) {
    msg.textContent = t('state.needChannel');
    return;
  }
  msg.textContent = t('ptz.connecting');
  const resp = await apiRaw(`/api/ptz/${channelId}/profiles`).catch(() => null);
  if (!resp) {
    msg.textContent = t('state.disconnected');
    return;
  }
  if (!resp.ok) {
    if (resp.status === 409) {
      msg.textContent = t('ptz.notSupported');
      return;
    }
    const info = await resp.json().catch(() => null);
    msg.textContent = t('ptz.connectFailed', { reason: info?.error ?? t('state.failed') });
    return;
  }
  const body = await resp.json().catch(() => null);
  const profiles = body?.profiles ?? [];
  if (profiles.length === 0) {
    msg.textContent = t('ptz.noProfiles');
    return;
  }
  ptzProfileToken = profiles[0].token;
  $('ptz-profile').innerHTML = profiles
    .map((p) => `<option value="${esc(p.token)}">${esc(p.label ?? p.name ?? p.token)}</option>`)
    .join('');
  $('ptz-profile').value = ptzProfileToken;
  msg.textContent = t('ptz.connected');
  await loadPtzPresets(channelId);
}

async function loadPtzPresets(channelId) {
  const resp = await apiRaw(`/api/ptz/${channelId}/presets?profileToken=${encodeURIComponent(ptzProfileToken)}`).catch(() => null);
  if (!resp || !resp.ok) {
    $('ptz-preset-list').innerHTML = '';
    return;
  }
  const list = await resp.json().catch(() => []);
  $('ptz-preset-list').innerHTML = (list ?? [])
    .map((p) => `<option value="${esc(p.token)}">${esc(p.name ?? p.token)}</option>`)
    .join('');
}

/** 共用回應處理：把後端錯誤（含閘門的 403）原文顯示，成功則顯示呼叫端給的字串。 */
async function ptzReport(resp, okText = '') {
  const msg = $('ptz-msg');
  if (!resp) {
    msg.textContent = t('state.disconnected');
    return false;
  }
  const info = await resp.json().catch(() => null);
  if (!resp.ok) {
    msg.textContent = t('ptz.moveFailed', { reason: info?.error ?? t('state.failed') });
    return false;
  }
  msg.textContent = okText;
  return true;
}

async function ptzMove(pan, tilt, zoom) {
  if (!ptzProfileToken) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const resp = await apiRaw(`/api/ptz/${channelId}/move`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken, pan, tilt, zoom }),
  }).catch(() => null);
  await ptzReport(resp);
}

async function ptzStop() {
  if (!ptzProfileToken) return;
  const channelId = Number($('ptz-channel').value);
  const resp = await apiRaw(`/api/ptz/${channelId}/stop`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken }),
  }).catch(() => null);
  await ptzReport(resp);
}

async function ptzHome() {
  if (!ptzProfileToken) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const resp = await apiRaw(`/api/ptz/${channelId}/home`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken }),
  }).catch(() => null);
  await ptzReport(resp, t('ptz.gonePreset'));
}

async function savePtzPreset() {
  if (!ptzProfileToken) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const typed = $('ptz-preset-name').value.trim();
  // 沒命名就用時間戳，與桌機版行為一致：儲存本身不該因為沒打字而失效。
  const name = typed || String(Date.now());
  const resp = await apiRaw(`/api/ptz/${channelId}/presets`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken, name }),
  }).catch(() => null);
  if (await ptzReport(resp, t('ptz.savedPreset'))) await loadPtzPresets(channelId);
}

async function gotoPtzPreset() {
  const token = $('ptz-preset-list').value;
  if (!ptzProfileToken || !token) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const resp = await apiRaw(`/api/ptz/${channelId}/presets/${encodeURIComponent(token)}/goto`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken }),
  }).catch(() => null);
  await ptzReport(resp, t('ptz.gonePreset'));
}

async function deletePtzPreset() {
  const token = $('ptz-preset-list').value;
  if (!ptzProfileToken || !token) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const resp = await apiRaw(`/api/ptz/${channelId}/presets/${encodeURIComponent(token)}?profileToken=${encodeURIComponent(ptzProfileToken)}`, {
    method: 'DELETE',
  }).catch(() => null);
  if (await ptzReport(resp, t('ptz.deletedPreset'))) await loadPtzPresets(channelId);
}

/**
 * 方向盤與變焦採「按住移動、放開停止」：ONVIF 的 ContinuousMove 是速度命令，
 * 不是單步位移，點一下就走一步的相機會在放開後持續移動。
 */
function bindPtzPad() {
  for (const btn of document.querySelectorAll('[data-move]')) {
    const issue = (ev) => {
      ev.preventDefault();
      const [pan, tilt, zoom] = btn.dataset.move.split(',').map(Number);
      void ptzMove(pan, tilt, zoom);
    };
    btn.addEventListener('pointerdown', issue);
    btn.addEventListener('keydown', (ev) => {
      if (ev.key === 'Enter' || ev.key === ' ') {
        ev.preventDefault();
        issue(ev);
      }
    });
    btn.addEventListener('keyup', () => void ptzStop());
  }
  document.addEventListener('pointerup', () => void ptzStop());
  $('ptz-stop').addEventListener('click', () => void ptzStop());
  $('ptz-home').addEventListener('click', () => void ptzHome());
}

/**
 * 攝影機 IO 接點（乾接點）。DI 只列出端子——ONVIF 沒有「輪詢 DI 目前狀態」的操作，
 * 輸入狀態只在事件中回報；DO 則可以 SetRelayOutputState 直接切換。
 * 「不支援 IO」由後端的 409 表達（與 PTZ 同一套語意），所以這裡要看狀態碼而不是只看 ok。
 */
async function loadIo(event) {
  if (event) event.preventDefault();
  const channelId = Number($('io-channel').value);
  const msg = $('io-msg');
  if (!Number.isInteger(channelId) || channelId <= 0) {
    msg.textContent = t('state.needChannel');
    return;
  }
  msg.textContent = t('state.loading');
  const inputsResp = await apiRaw(`/api/io/${channelId}/inputs`).catch(() => null);
  if (!inputsResp) {
    msg.textContent = t('state.disconnected');
    return;
  }
  if (inputsResp.status === 409) {
    renderIoInputs([]);
    renderIoOutputs([]);
    msg.textContent = t('io.notSupported');
    return;
  }
  if (!inputsResp.ok) {
    const info = await inputsResp.json().catch(() => null);
    msg.textContent = t('io.loadFailed', { reason: info?.error ?? inputsResp.status });
    return;
  }
  const inputs = (await inputsResp.json()).inputs ?? [];
  const outputsResp = await apiRaw(`/api/io/${channelId}/outputs`).catch(() => null);
  const outputs = outputsResp?.ok ? ((await outputsResp.json()).outputs ?? []) : [];
  renderIoInputs(inputs);
  renderIoOutputs(outputs);
  msg.textContent = t('io.loaded', { inputs: inputs.length, outputs: outputs.length });
}

function renderIoInputs(rows) {
  $('io-inputs').querySelector('tbody').innerHTML = rows
    .map((r) => `<tr><td>${esc(r.token)}</td><td>${esc(r.name || '—')}</td><td>${esc(r.label || '—')}</td></tr>`)
    .join('');
}

function renderIoOutputs(rows) {
  $('io-outputs').querySelector('tbody').innerHTML = rows
    .map((r) => `<tr><td>${esc(r.token)}</td><td>${esc(r.name || '—')}</td><td>${esc(r.label || '—')}</td><td><button type="button" data-io-relay="${esc(r.token)}" data-io-active="1">${tx('io.activate')}</button> <button type="button" data-io-relay="${esc(r.token)}" data-io-active="0">${tx('io.deactivate')}</button></td></tr>`)
    .join('');
}

async function switchIoRelay(button) {
  const channelId = Number($('io-channel').value);
  const token = button.dataset.ioRelay;
  const active = button.dataset.ioActive === '1';
  const msg = $('io-msg');
  const resp = await apiRaw(`/api/io/${channelId}/outputs/${encodeURIComponent(token)}/state`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ active }),
  }).catch(() => null);
  if (!resp) {
    msg.textContent = t('state.disconnected');
    return;
  }
  if (resp.ok) {
    msg.textContent = t('io.switched', { token });
    return;
  }
  const info = await resp.json().catch(() => null);
  msg.textContent = t('io.switchFailed', { reason: info?.error ?? resp.status });
}

// 必須是函式而不是模組層級常數：常數會在 import 時就把譯文定死，切換語言後這張表不會更新。
const FORENSIC_SOURCE_NAMES = { 1: () => t('replay.sourceAlarm'), 2: () => t('replay.source'), 4: () => 'POS', 8: () => t('replay.sourceEdgeAi') };

async function searchEvents(raw) {
  const q = raw.trim();
  if (!q) {
    $('events-msg').textContent = t('state.needKeyword');
    return;
  }
  const limit = 50;
  const to = new Date();
  const from = new Date(to.getTime() - 24 * 3600 * 1000);
  const params = new URLSearchParams({ q, from: from.toISOString(), to: to.toISOString(), limit: String(limit) });
  const page = await api(`/api/events/search?${params}`).catch(() => null);
  if (!page) {
    $('events-msg').textContent = t('state.disconnected');
    return;
  }
  const items = page.items ?? [];
  $('events').querySelector('tbody').innerHTML = items
    .map((hit) => {
      const { id, time, source, text, status } = eventRow(hit);
      return `<tr><td>${esc(id ? '#' + id : '')}</td><td>${esc(time)}</td><td>${esc(FORENSIC_SOURCE_NAMES[source]?.() ?? '—')}</td><td>${esc(text || '—')}</td><td>${esc(status || '—')}</td></tr>`;
    })
    .join('');
  if (items.length === 0) {
    $('events-msg').textContent = t('state.noResults');
  } else if (items.length >= limit) {
    $('events-msg').textContent = t('search.foundCapped', { total: items.length, shown: items.length });
  } else {
    $('events-msg').textContent = t('search.found', { total: items.length });
  }
}

/** 設定頁：GET /api/config 讀回目前值；configCard 正規化欄位大小寫與預設值。 */
async function loadSettings() {
  const msg = $('settings-msg');
  try {
    const card = configCard(await api('/api/config'));
    $('set-auth-enabled').checked = card.authEnabled;
    $('set-lock-threshold').value = String(card.lockoutThreshold);
    $('set-lock-minutes').value = String(card.lockoutMinutes);
    $('set-retention-days').value = String(card.recordingRetentionDays);
    $('set-watermark-gb').value = String(card.recordingWatermarkGb);
    $('set-alarm-retention').value = String(card.alarmRetentionDays);
    $('set-audit-retention').value = String(card.auditRetentionDays);
    if (msg) msg.textContent = '';
  } catch (err) {
    if (msg) msg.textContent = t('setting.loadFailed');
  }
  await refreshUsage();
}

/** 用量唯讀摘要。文字在執行時產生，所以切換語系時也要重繪（見 applyLocale）。 */
async function refreshUsage() {
  const el = $('settings-usage');
  if (!el) return;
  try {
    const usage = await api('/api/config/usage');
    el.textContent = t('setting.usage', { gb: Number(usage.gb ?? 0).toFixed(1), days: usage.retentionDays });
  } catch (err) {
    el.textContent = '';
  }
}

// ── 裝置資訊（對齊 QCV8001 web 的 Info 頁）────────────────────────────────
//
// 雙欄表格：左「系統」（產品／版本／時間／授權）、右「儲存與狀態」（用量／容量／健康）。
// 資料全部來自既有 API；/config/usage 會做資料夾層級對帳（ReconcileUsage）所以只在
// 開啟分頁時載入，不進 12 秒輪詢——每 12 秒掃一次錄影目錄是白花的磁碟成本。

/** 位元組 → 人類可讀；小數一位，避免表格欄寬抖動。 */
function formatBytes(bytes) {
  const value = Number(bytes ?? 0);
  if (!Number.isFinite(value)) return '—';
  const units = ['B', 'KB', 'MB', 'GB', 'TB'];
  let unit = 0;
  let size = value;
  while (size >= 1024 && unit < units.length - 1) {
    size /= 1024;
    unit += 1;
  }
  return `${size.toFixed(unit === 0 ? 0 : 1)} ${units[unit]}`;
}

/** 剩餘可錄秒數 → 「N 天 H 小時」；狀態不是 known 時顯示佔位（—），不能印 0。 */
function formatRemaining(seconds, state) {
  if (state !== 'known') return '—';
  const total = Math.max(0, Math.floor(Number(seconds ?? 0) / 60));
  const days = Math.floor(total / 1440);
  const hours = Math.floor((total % 1440) / 60);
  if (days > 0) return t('info.remainDays', { days, hours });
  return t('info.remainHours', { hours, minutes: total % 60 });
}

/** 授權／容量／磁碟健康的狀態字串都來自伺服器，前端只補翻譯；未知值保留原字串。 */
function localizedState(key, state) {
  const value = String(state ?? '');
  const text = t(`${key}.${value}`);
  return text === `${key}.${value}` ? value : text;
}

async function refreshDeviceInfo() {
  const msg = $('info-msg');
  const systemBody = $('info-system')?.querySelector('tbody');
  const storageBody = $('info-storage')?.querySelector('tbody');
  const driveBody = $('info-drives')?.querySelector('tbody');
  if (!systemBody || !storageBody || !driveBody) return;

  // 五個來源各自容錯：任何一個讀不到，其他欄位照常顯示，那一格寫「—」。
  const [health, license, usage, capacity, disks] = await Promise.all([
    api('/api/health').catch(() => null),
    api('/api/license').catch(() => null),
    api('/api/config/usage').catch(() => null),
    api('/api/recording/capacity').catch(() => null),
    api('/api/system/disk-health').catch(() => null),
  ]);

  const session = readSession();
  const dash = '—';
  // 授權拆成兩列：狀態（未授權時附伺服器的說明）與續期提醒（伺服端算好的文案）。
  // 這些先算成「未跳脫」的原文，模板裡一律 esc(...) 一次跳脫（安全測試的規則：
  // innerHTML 模板的每個插值都必須以 esc/tx/Number 開頭，所以不能插變數本身）。
  const licenseState = license
    ? license.decision === 'Valid'
      ? license.decision
      : `${license.decision}${license.message ? ` · ${license.message}` : ''}`
    : dash;
  const licenseExpiry = license?.expiryMessage || dash;
  const channelsRaw = health ? t('info.channelsLine', { count: Number(health.channels) }) : dash;
  const usageRaw = usage ? t('info.usageLine', { gb: Number(usage.gb ?? 0).toFixed(1), days: usage.retentionDays }) : dash;
  const capacityRaw = capacity
    ? t('info.capacityLine', {
        state: localizedState('info.capacityState', capacity.state),
        remain: formatRemaining(capacity.remainingSeconds, capacity.state),
      })
    : dash;
  const diskRaw = disks
    ? t('info.diskHealthLine', {
        state: localizedState('info.diskState', disks.state),
        failing: Number(disks.failing ?? 0),
        warning: Number(disks.warning ?? 0),
      })
    : dash;

  systemBody.innerHTML = [
    `<tr><th scope="row">${esc(t('info.product'))}</th><td>${esc('HeliVMS')}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.database'))}</th><td>${esc(health?.database ?? dash)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.serverTime'))}</th><td>${esc(`${new Date().toLocaleString()} (${Intl.DateTimeFormat().resolvedOptions().timeZone})`)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.signedIn'))}</th><td>${esc(session ? `${session.name || ''}`.trim() || session.role : t('state.notSignedIn'))}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.licenseState'))}</th><td>${esc(licenseState)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.licenseChannels'))}</th><td>${esc(license?.maxCameras != null ? String(license.maxCameras) : dash)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.licenseExpiry'))}</th><td>${esc(licenseExpiry)}</td></tr>`,
  ].join('');

  storageBody.innerHTML = [
    `<tr><th scope="row">${esc(t('info.channels'))}</th><td>${esc(channelsRaw)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.usage'))}</th><td>${esc(usageRaw)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.capacity'))}</th><td>${esc(capacityRaw)}</td></tr>`,
    `<tr><th scope="row">${esc(t('info.diskHealth'))}</th><td>${esc(diskRaw)}</td></tr>`,
  ].join('');

  const drives = disks?.drives ?? [];
  const byRoot = new Map((capacity?.drives ?? []).map((d) => [String(d.root), d]));
  driveBody.innerHTML = drives.length > 0
    ? drives
        .map((d) => {
          const cap = byRoot.get(String(d.root));
          return `<tr><td>${esc(d.root)}</td><td>${esc(d.model || dash)}</td><td>${esc(localizedState('info.diskState', d.state))}</td><td>${esc(d.temperatureC != null ? `${d.temperatureC}°C` : dash)}</td><td>${esc(cap ? formatBytes(cap.freeBytes) : dash)}</td><td>${esc(cap ? formatRemaining(cap.remainingSeconds, cap.state) : dash)}</td></tr>`;
        })
        .join('')
    : `<tr><td colspan="6">${esc(t('state.noResults'))}</td></tr>`;

  if (msg) msg.textContent = t('info.updatedAt', { time: new Date().toLocaleTimeString() });
}

// ── 匯出（對齊 QCV8001 web 的 Export／備份頁）────────────────────────────
//
// 工作清單吃既有 GET /api/exports；建立走 POST /api/exports（伺服器排程 ffmpeg）。
// 狀態徽章隨 12 秒輪詢刷新，操作員不用手動重整就知道工作跑完沒有。

/** 匯出工作的狀態值（伺服器小寫英文）→ 徽章樣式；未知的狀態沿用中性樣式。 */
function exportStatusClass(status) {
  switch (String(status ?? '')) {
    case 'finished':
      return 'export-status ok';
    case 'failed':
      return 'export-status fail';
    case 'running':
      return 'export-status run';
    default:
      return 'export-status';
  }
}

async function refreshExports() {
  const body = $('export-jobs')?.querySelector('tbody');
  if (!body) return;
  const jobs = await api('/api/exports').catch(() => []);
  const rows = Array.isArray(jobs) ? jobs : [];
  body.innerHTML = rows.length > 0
    ? rows
        .map((j) => `<tr><td>${Number(j.id)}</td><td>${esc(channelName(Number(j.channelId)))}</td><td class="wrap">${esc(formatTimestamp(j.startUtc))} – ${esc(formatTimestamp(j.endUtc))}</td><td><span class="${esc(exportStatusClass(j.status))}">${esc(localizedState('export.status', j.status))}</span></td><td>${esc(j.fileSizeBytes != null ? formatBytes(j.fileSizeBytes) : '—')}</td><td>${esc(formatTimestamp(j.createdUtc))}</td><td class="wrap">${esc(j.error || '—')}</td></tr>`)
        .join('')
    : `<tr><td colspan="7">${esc(t('export.empty'))}</td></tr>`;
  const meta = $('export-meta');
  if (meta) meta.textContent = rows.length > 0 ? t('export.listMeta', { count: rows.length }) : '';
}

async function submitExport(event) {
  event.preventDefault();
  const msg = $('export-msg');
  const from = $('export-from')?.value;
  const to = $('export-to')?.value;
  const channelId = Number($('export-channel')?.value);
  if (!from || !to || !Number.isInteger(channelId) || channelId < 1) {
    if (msg) msg.textContent = t('export.needFields');
    return;
  }

  // datetime-local 是本地時間；API 收 UTC ISO 字串（與遠程回放的提交同一種換算）。
  const body = {
    channelId,
    stream: 'main',
    fromUtc: new Date(from).toISOString(),
    toUtc: new Date(to).toISOString(),
  };
  try {
    const response = await apiRaw('/api/exports', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      const info = await response.json().catch(() => null);
      if (msg) msg.textContent = info?.error ?? t('export.createFailed');
      return;
    }
    if (msg) msg.textContent = t('export.created');
    await refreshExports();
  } catch (err) {
    if (msg) msg.textContent = t('export.createFailed');
  }
}

/** 匯出分頁的頻道下拉與預設時段（今天 00:00 → 現在）。 */
function refreshExportSources() {
  const select = $('export-channel');
  if (select) {
    const current = select.value;
    select.innerHTML = localChannels
      .map((c) => `<option value="${Number(c.id)}">${esc(c.name)}</option>`)
      .join('');
    if (current && localChannels.some((c) => String(c.id) === current)) {
      select.value = current;
    } else if (localChannels.length > 0) {
      select.value = String(Number(localChannels[0].id));
    }
  }
  const from = $('export-from');
  const to = $('export-to');
  if (from && !from.value) {
    const start = new Date();
    start.setHours(0, 0, 0, 0);
    from.value = toLocalInputValue(start);
  }
  if (to && !to.value) to.value = toLocalInputValue(new Date());
}

/** datetime-local 欄位要的是本地時間（YYYY-MM-DDTHH:mm），不是 ISO UTC。 */
function toLocalInputValue(date) {
  const pad = (n) => String(n).padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}`;
}

/** 寫回設定。範圍驗證在伺服器端（各欄上限不同），錯誤訊息照後端回傳顯示。 */
async function saveSettings(event) {
  event.preventDefault();
  const msg = $('settings-msg');
  const body = {
    authEnabled: $('set-auth-enabled').checked,
    lockoutThreshold: Number($('set-lock-threshold').value),
    lockoutMinutes: Number($('set-lock-minutes').value),
    recordingRetentionDays: Number($('set-retention-days').value),
    recordingWatermarkGb: Number($('set-watermark-gb').value),
    alarmRetentionDays: Number($('set-alarm-retention').value),
    auditRetentionDays: Number($('set-audit-retention').value),
  };
  try {
    const response = await apiRaw('/api/config', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    });
    if (!response.ok) {
      const info = await response.json().catch(() => null);
      if (msg) msg.textContent = info?.error ?? t('setting.saveFailed');
      return;
    }
    if (msg) msg.textContent = t('setting.saved');
    await loadSettings();
  } catch (err) {
    if (msg) msg.textContent = t('setting.saveFailed');
  }
}

let alertSocket = null;

function connectLive() {
  const proto = location.protocol === 'https:' ? 'wss' : 'ws';
  // 走 createAlertSocket 而不是直接 new WebSocket：斷線要自己接回來。
  // 現場症狀是「伺服器重開機之後主控台再也不跳告警」，畫面上只有一個 WS off 的小字。
  alertSocket = createAlertSocket({
    url: () => `${proto}://${location.host}/api/alerts/ws?key=${encodeURIComponent(key())}`,
    onMessage: (data) => {
      const msg = parseWsMessage(data);
      if (!msg) return;
      const li = document.createElement('li');
      li.textContent = `${formatTimestamp(new Date().toISOString())} ${msg.kind} #${msg.eventId} ${msg.priority ?? msg.status ?? ''}`;
      $('live-log').prepend(li);
    },
    onStatus: ({ online }) => {
      $('live').textContent = online ? 'WS on' : 'WS off';
      $('live').classList.toggle('off', !online);
      $('live').classList.toggle('on', online);
    },
  });
  alertSocket.connect();
}

/** 登出或離開頁面時停掉重連，否則會在背景繼續嘗試。 */
function disconnectLive() {
  alertSocket?.close();
  alertSocket = null;
}

function wire() {
  $('login').addEventListener('submit', submitLogin);
  $('logout').addEventListener('click', (e) => {
    e.preventDefault();
    logout();
  });

  bindTabs();
  showTab('live');

  $('live-form').addEventListener('submit', openLiveView);
  $('live-stop').addEventListener('click', stopLiveView);
  // 切版面立即重畫空格線（並收掉正在跑的會話，因為格數變了）。
  $('live-layout').addEventListener('change', () => {
    void stopLiveView();
    renderLiveGrid();
  });
  renderLiveGrid();

  $('snap-form').addEventListener('submit', openSnapshotView);
  $('snap-stop').addEventListener('click', stopSnapshotView);
  $('snap-capture').addEventListener('click', captureSnapshot);

  $('ptz-form').addEventListener('submit', loadPtz);
  $('ptz-preset-save').addEventListener('click', savePtzPreset);
  $('ptz-preset-goto').addEventListener('click', gotoPtzPreset);
  $('ptz-preset-delete').addEventListener('click', deletePtzPreset);
  bindPtzPad();

  $('io-form').addEventListener('submit', loadIo);
  // 每一列都是動態產生的，用事件委派才不會在重繪後漏掉監聽。
  $('io-outputs').addEventListener('click', (e) => {
    const btn = e.target.closest('[data-io-relay]');
    if (btn) switchIoRelay(btn).catch(console.error);
  });

  $('settings-form').addEventListener('submit', saveSettings);

  $('export-form').addEventListener('submit', submitExport);
  $('export-refresh').addEventListener('click', () => {
    void refreshExports();
  });
  $('info-refresh').addEventListener('click', () => {
    void refreshDeviceInfo();
  });

  $('play-form').addEventListener('submit', playRemote);
  // 換來源節點時同步換頻道建議（M302），否則會留著上一台節點的頻道清單。
  $('play-node')?.addEventListener('change', fillPlayChannels);
  // 拖動游標只發 seeked、播放才發 timeupdate，兩個都聽時鐘才不會停在舊位置。
  $('play-video').addEventListener('timeupdate', renderPlayClock);
  $('play-video').addEventListener('seeked', renderPlayClock);
  defaultPlayWindow();

  $('search-form').addEventListener('submit', (e) => {
    e.preventDefault();
    searchEvents($('q').value).catch(console.error);
  });

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
  $('apikey').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      refreshAll();
    }
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
 * 套用語系。除了靜態標記（applyDom），訊息列與動態清單也必須重繪——
 * 它們是執行時用 t() 產生的，不會因為 applyDom 而改變。
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
  await refreshUsage();
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
  bindLocalePicker();
  connectLive();
  // 離開頁面時明確停掉重連。沒有這行的話 pagehide 之後重連計時器還在跑，
  // 會在背景不斷嘗試連線直到分頁被回收。
  window.addEventListener('pagehide', disconnectLive);
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
      ? `${status.message || status.decision} ${txMissing(missing)}`
      : status.message || status.decision;
    el.textContent = t('state.unauthorized', { reason });
    el.classList.add('off');
  } catch (err) {
    if (el) el.textContent = '';
  }
}

/** 授權缺項的句子；與 refreshLicense 分開，讓那支函式維持完全不碰 HTML。 */
function txMissing(features) {
  return t('state.missingFeatures', { features: features.join(t('list.separator')) });
}

async function refreshAll() {
  // 匯出工作清單一起輪詢：工作排程在伺服器端（ffmpeg），徽章不刷新的話
  // 操作員要手動重整才知道跑完沒有。
  await Promise.allSettled([refreshHealth(), refreshPlaySources(), refreshLocalChannels(), refreshExports()]);
}

boot();
