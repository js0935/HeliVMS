import { applyDom, DEFAULT_LOCALE, locale, setLocale, t } from './i18n.js';
import { createAlertSocket } from './alerts.js';
import { createLiveViewer } from './live.js';
import { apiRaw, clearApiKey, hasAccess, key, readSession, storeSession } from './auth.js';
import {
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
  parseM3u8,
  parseWsMessage,
  playbackBufferedFor,
  playbackNeedsSegment,
  playbackRangesAheadToEvict,
  playbackRangesToEvict,
  playbackSeamStart,
  playlistMediaStarts,
  playlistSegmentIndexAt,
  pollGate,
  tx,
} from './lib.js';

const $ = (id) => document.getElementById(id);
const POLL_MS = 12000; // 授權與健康狀態的輪詢；分頁隱藏或焦點在控制項上時暫停。
// apiRaw / key / session 讀寫都在 auth.js：登入頁與主介面共用同一份 transport 與憑證。

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

async function logout() {
  // 撤銷伺服器端工作階段（M306）：token 一撤銷，帶著它的請求都回 401。
  // best-effort——連不上時仍要清掉本機憑證，伺服器端最終也會因過期而失效。
  try {
    await apiRaw('/api/accounts/logout', { method: 'POST' });
  } catch {
    // 忽略：頁面仍要回到登入頁並清除本機狀態。
  }
  storeSession(null);
  clearApiKey();
  stopLiveView();
  stopSnapshotView();
  window.location.replace('login.html');
}

function updateChrome() {
  const session = readSession();
  const who = $('who');
  who.textContent = session ? `${session.role} · ${session.name ?? ''}`.trim() : t('state.notSignedIn');
  who.classList.toggle('on', Boolean(session));
  $('logout').hidden = !session;
}

/** 通過登入頁之後才啟動需要憑證的輪詢與 WebSocket，未登入時不製造一堆 401。 */
async function enterApp() {
  $('app-shell').hidden = false;
  updateChrome();
  await refreshHealth();
  await refreshLicense();
  await refreshAll();
  if (!alertSocket) connectLive();
  // 沒有選台清單也沒有「開啟」按鈕，進來就先照目前版面開第一批，
  // 否則操作員看到的是一格空畫面，而且不知道該按哪裡才會有影像。
  void openLiveView();
}

/** 目前選取的次分頁（group → name）。切主頁籤再回來時要回到操作員最後看的那一頁。 */
const activeSubTab = new Map();

/**
 * 次分頁（主頁內的區段）。
 *
 * 每一組各自獨立：`data-subtab-group` 標在工具列上、`data-subpanel-group` 標在容器上，
 * 切「監看 → 截圖」不會動到設定頁的選取。切換只改可見性與 ARIA，不重建內容，
 * 所以切去截圖再切回來，監看牆上的 WebRTC 會話不會斷。
 */
function showSubTab(group, name) {
  activeSubTab.set(group, name);
  const bar = document.querySelector(`[data-subtab-group="${group}"]`);
  for (const btn of bar?.querySelectorAll('[data-subtab]') ?? []) {
    const on = btn.dataset.subtab === name;
    btn.classList.toggle('active', on);
    btn.setAttribute('aria-selected', String(on));
  }
  const holder = document.querySelector(`[data-subpanel-group="${group}"]`);
  for (const panel of holder?.querySelectorAll('[data-subpanel]') ?? []) {
    panel.hidden = panel.dataset.subpanel !== name;
  }

  // 開啟次分頁才載入它自己的資料：裝置資訊的 /config/usage 會掃錄影目錄
  // （ReconcileUsage），匯出清單則要問一次工作佇列——兩者都不該在別的頁面被輪詢。
  if (group === 'settings' && name === 'info') void refreshDeviceInfo();
  if (group === 'playback' && name === 'export') {
    refreshExportSources();
    void refreshExports();
  }
}

/**
 * 從監看牆的某一格跳到它的工具頁（截圖／PTZ），並把該格的頻道帶過去。
 *
 * 這是「功能雜亂」的另一半：把截圖與 PTZ 收進監看頁之後，操作員不該還得自己
 * 記住「我在看第幾台、編號是多少」再手打一次——那一格就是答案。
 */
function openCellTool(group, name, channelId) {
  showSubTab(group, name);
  const target = name === 'snapshot' ? $('snap-channel') : $('ptz-channel');
  if (target && Number.isInteger(channelId) && channelId > 0) {
    target.value = String(channelId);
  }
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
  // 回到主頁時把它自己的次分頁還原：每組只有一個可見，標記如實反映現況。
  for (const [group, sub] of activeSubTab) {
    showSubTab(group, sub);
  }
  // 設定值不隨輪詢重載：每 12 秒覆寫會蓋掉正在編輯的欄位，所以只在開啟設定分頁時載入。
  if (name === 'settings') void loadSettings();
}

function bindTabs() {
  for (const btn of document.querySelectorAll('[data-tab]')) {
    btn.addEventListener('click', () => showTab(btn.dataset.tab));
  }
  // 次分頁按鈕只知道自己的 group 與 name，群組由它所屬的工具列決定。
  for (const btn of document.querySelectorAll('[data-subtab]')) {
    btn.addEventListener('click', () => {
      const group = btn.closest('[data-subtab-group]')?.dataset.subtabGroup;
      if (group) showSubTab(group, btn.dataset.subtab);
    });
  }
}

const snapshotViewer = createLiveViewer({ apiRaw, t });

/** 目前格線上的每一格各自一條 WHEP 會話；「停止」一次整批收掉。 */
let liveCells = [];

/**
 * 每次收檔或重新開播都遞增；連續按同一版面時，較早那一輪的協商結果不能回來
 * 覆蓋較新批次的訊息列（症狀是畫面是 5–8、文字還寫 1–4）。
 */
let liveOpenGeneration = 0;

const LIVE_LAYOUTS = [1, 4, 9, 16, 25];

/**
 * 目前版面與第幾批頻道。不再有選台清單與「開啟」按鈕：按幾分割就開幾路，
 * 再按同一個版面就往下一批。頁次用模運算循環，到底自然回到第一批。
 */
let liveLayout = 1;
let livePage = 0;

/** 版面值就是格數，開根號即為每列格數。 */
function liveLayoutCount() {
  return LIVE_LAYOUTS.includes(liveLayout) ? liveLayout : 1;
}

/** 依目前版面與頁次取出這一輪的頻道；不足一整批時就少開幾路。 */
function livePageChannels() {
  const size = liveLayoutCount();
  if (localChannels.length === 0 || size < 1) return [];
  const pages = Math.ceil(localChannels.length / size);
  const page = livePage % pages;
  return localChannels.slice(page * size, page * size + size);
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
  // 版面按鈕同時是分頁器：讓「現在看的是幾分割」在按鈕上直接看得出來。
  // aria-pressed 讓螢幕閱讀器唸得出目前版面，而不是只靠底色。
  for (const btn of $('live-layout')?.querySelectorAll('[data-layout]') ?? []) {
    const active = Number(btn.dataset.layout) === liveLayout;
    btn.classList.toggle('active', active);
    btn.setAttribute('aria-pressed', String(active));
  }
  // 每一格自帶工具列：全螢幕、截圖、PTZ。動作跟著「那一格」而不是面板的某一頁，
  // 否則按了截圖卻抓到另一台。符號只是提示，真正的名稱放在 aria-label 與 title
  // （螢幕閱讀器唸得出「全螢幕」，唸不出「⛶」）。
  grid.innerHTML = Array.from({ length: count }, () =>
    '<div class="live-cell"><video class="player" autoplay muted playsinline></video>'
    + '<span class="live-cell-label"></span>'
    + '<div class="live-cell-actions">'
    + `<button type="button" class="cell-btn" data-cell-action="fullscreen" title="${tx('cell.fullscreen')}" aria-label="${tx('cell.fullscreen')}">⛶</button>`
    + `<button type="button" class="cell-btn" data-cell-action="snapshot" title="${tx('cell.snapshot')}" aria-label="${tx('cell.snapshot')}">⎙</button>`
    + `<button type="button" class="cell-btn" data-cell-action="ptz" title="${tx('cell.ptz')}" aria-label="${tx('cell.ptz')}">⌖</button>`
    + '</div></div>',
  ).join('');
}

/** 第幾個格子（依畫面上的順序）；找不到回 -1。 */
function liveCellIndex(cell) {
  return [...($('live-grid')?.querySelectorAll('.live-cell') ?? [])].indexOf(cell);
}

/** 把某個格子上目前解碼中的畫面存成 PNG。 */
function captureCellFrame(video, channelId) {
  const msg = $('live-msg');
  if (!video?.videoWidth) {
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
    link.download = `snapshot-ch${channelId || 0}-${Date.now()}.png`;
    link.click();
    URL.revokeObjectURL(url);
    msg.textContent = t('state.snapshotDone');
  }, 'image/png');
}

/**
 * 監看牆上每一格的工具列（事件代理）。
 *
 * 用代理而不是逐格綁定：格線每次開檔都整批重畫，逐格綁定會漏掉重畫後的按鈕，
 * 症狀是「大格數時有幾格的按鈕沒反應」。
 */
function onLiveGridClick(event) {
  const btn = event.target.closest('[data-cell-action]');
  if (!btn) return;
  const cell = btn.closest('.live-cell');
  const entry = liveCells[liveCellIndex(cell)];
  // 先取這一格自己的頻道；剛換版面還沒開播時退回這一格在目前批次中的位置。
  const channelId = Number(entry?.channelId ?? 0)
    || Number(livePageChannels()[liveCellIndex(cell)]?.id ?? 0);

  if (btn.dataset.cellAction === 'fullscreen') {
    // 全螢幕失敗（瀏覽器政策、非使用者手勢）不該讓畫面壞掉，靜默即可。
    cell?.requestFullscreen?.().catch(() => {});
    return;
  }

  if (btn.dataset.cellAction === 'snapshot') {
    captureCellFrame(entry?.video, channelId);
    return;
  }

  openCellTool('live', 'ptz', channelId);
}

/**
 * 多分割即時監看：版面決定格數、頁次決定看哪一批頻道；每一格各自一條 WHEP 會話。
 *
 * 沒有選台清單：按了幾分割就開幾路，再按同一版面就換下一批。
 * 刻意平行開檔：ICE 收集最長 3 秒，逐格等待會讓 5x5 等到天荒地老。
 */
async function openLiveView() {
  const generation = ++liveOpenGeneration;
  await stopLiveView({ external: false });
  if (generation !== liveOpenGeneration) return;
  const msg = $('live-msg');
  const shown = livePageChannels();

  renderLiveGrid();

  if (shown.length === 0) {
    msg.textContent = t('live.noChannels');
    $('live-meta').textContent = '';
    return;
  }

  const cells = [...$('live-grid').querySelectorAll('.live-cell')];
  const pages = Math.ceil(localChannels.length / liveLayoutCount());
  const page = (livePage % pages) + 1;

  // 一格的畫面佔滿整個監看區（放大看車牌），多格則每一格只有幾百像素寬。
  // 多格要次碼流：主流是 1080p，伺服器端要先解碼再重新編碼，首包實測 2.6–24.3 秒
  // （最慢那台 24 秒＝「有一格一直沒有畫面」），次碼流全部 2.2–3.0 秒。
  // 桌面監看牆用的是同一條規則（單格才用主流）。
  const stream = shown.length > 1 ? 'sub' : 'main';

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
      }, { stream }),
    ),
  );
  if (generation !== liveOpenGeneration) return;

  const ok = results.filter(Boolean).length;
  // 這裡只證明「協商成功」，第一張影格還沒到；真正的「播放中」由每格自己的
  // waitForFirstFrame 回報（state.liveReady）。把這句寫成「已就緒」會把
  // 連得上卻沒有影像（防火牆擋 UDP）誤報成正常，操作員只看到一片黑。
  msg.textContent = ok === liveCells.length && ok > 0
    ? t('live.connected', { ok })
    : t('live.someFailed', { ok, total: liveCells.length });
  $('live-meta').textContent = `${t('live.gridMeta', {
    count: ok,
    channels: shown.map(channelName).join(t('list.separator')),
  })} · ${t('live.pageMeta', { page, pages })}`;
}

/**
 * 分頁被關掉或換頁時，主動把每一格的 WHEP 會話收掉。
 *
 * 不做這件事的代價不是「等 30 秒就自己回收」——那一條會話永遠不會逾時（媒體還在流，
 * 每個封包都會刷新它的活動時間），於是觀看名額與每一路的 ffmpeg 都被一個不存在的
 * 瀏覽器繼續佔著。`stop()` 的 DELETE 帶 keepalive，所以在文件被丟掉之後仍送得出去。
 */
function releaseLiveSessionsOnUnload() {
  window.addEventListener('pagehide', (event) => {
    // persisted＝這一頁只是進到 back/forward 快取，等一下可能被還原；那不是真的離開，
    // 收掉連線會讓「上一頁」回來時只剩一格死畫面。
    if (event.persisted) return;
    for (const { viewer } of liveCells) void viewer.stop();
    liveCells = [];
  });
}

async function stopLiveView({ external = true } = {}) {
  // 開播前收掉上一批不算「外部停止」，不能再遞一代，否則自己的結果會被自己作廢。
  if (external) liveOpenGeneration += 1;
  const generation = liveOpenGeneration;
  if (liveCells.length === 0) return;
  const cells = liveCells;
  liveCells = [];
  const results = await Promise.all(cells.map(({ viewer }) => viewer.stop()));
  if (generation !== liveOpenGeneration) return;
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

  // 即時監看不再有選單：頻道順序就是分頁順序，輪詢更新 localChannels 後，
  // 下一次按版面就會用到新的清單，不必保留任何選取狀態。

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

/** 目前播放中的遠程回放：換一段或關頁前要停掉，否則舊管線會繼續抓檔案。 */
let activePlayback = null;

/** TimeRanges 只能逐索引取，轉成純陣列才好交給 lib.js 的純函式判斷。 */
function readBufferedRanges(buffer) {
  const ranges = [];
  for (let i = 0; i < buffer.buffered.length; i++) {
    ranges.push({ start: buffer.buffered.start(i), end: buffer.buffered.end(i) });
  }
  return ranges;
}

/** 等 SourceBuffer 空出來：append 與 remove 都以 updateend 收尾，error 就當場判失敗。 */
function whenBufferIdle(buffer, message) {
  return new Promise((resolve, reject) => {
    const cleanup = () => {
      buffer.removeEventListener('updateend', done);
      buffer.removeEventListener('error', fail);
    };
    const done = () => {
      cleanup();
      resolve();
    };
    const fail = () => {
      cleanup();
      reject(new Error(message));
    };
    buffer.addEventListener('updateend', done);
    buffer.addEventListener('error', fail);
  });
}

/**
 * 遠程回放（M239，§14.3 串流 P0）：HLS VOD → MediaSource。
 * 伺服器每個錄影檔自帶 ftyp＋moov，所以初始化段只送第一段的那一份，
 * 之後把每段的 moof/mdat 依序 append 就能在瀏覽器裡播。
 *
 * 每個錄影檔是獨立的 ffmpeg 行程寫的，時間戳都從 0 重新開始；不設 timestampOffset
 * 直接 append，後段的幀會覆蓋到前段的時間軸上，實際只剩第一段能播。所以要以
 * parseFmp4VideoSpan 量出的真實媒體長度累加偏移，把後段接到前段之後。
 *
 * **一次 append 整份清單會撞上 SourceBuffer 的固定配額**（實機：40 分鐘的窗就得到
 * 「The SourceBuffer is full, and cannot free space to append additional buffers」，
 * 畫面黑屏、訊息停在載入中，而即時監看完全正常）。所以改成邊播邊補：只有播放頭
 * 接近已緩衝尾端時才抓下一段，append 前先把播放頭後方的舊資料 remove 掉。
 *
 * 回傳 `{ timeline, ready, done }`：`ready` 在第一段真的 append 完成時 resolve
 * （呼叫端才能把時鐘接上、顯示就緒），`done` 代表整條管線補完或失敗。`timeline`
 * 是活的陣列，每補一段就更新，`options.onTimeline` 會拿到它。
 */
async function attachPlaylist(video, playlist, options = {}) {
  if (!('MediaSource' in window)) {
    throw new Error(t('state.mseUnsupported'));
  }

  const type = mseMimeType(
    video.canPlayType('video/mp4; codecs="avc1.64001f,mp4a.40.2"'),
    video.canPlayType('video/mp4; codecs="avc1.42E01E"'),
  );

  const media = new MediaSource();
  const objectUrl = URL.createObjectURL(media);
  video.src = objectUrl;

  await new Promise((resolve) => media.addEventListener('sourceopen', resolve, { once: true }));
  const buffer = media.addSourceBuffer(type);

  const timeline = [];
  // 每一段的媒體時間軸起點（秒）＝清單 EXTINF 的累計位置。位置一律取這裡，任何一段
  // 都能單獨接上去：拖到還沒抓過的時段時直接跳到那一段，不必從讀取頭逐段補。
  const mediaStarts = playlistMediaStarts(playlist.segments);
  let tracks = null;
  let nextIndex = 0;
  let pumping = false;
  let stopped = false;
  let failure = null;
  let appendedAny = false;

  let resolveReady;
  let rejectReady;
  let resolveDone;
  let rejectDone;
  const ready = new Promise((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  const done = new Promise((resolve, reject) => {
    resolveDone = resolve;
    rejectDone = reject;
  });
  // 呼叫端不一定會接住（第一段就失敗時 playRemote 只 await ready），沒有這個 catch
  // 會變成 unhandled rejection，把真正的錯誤訊息埋掉。
  ready.catch(() => {});
  done.catch(() => {});

  const onProgress = () => {
    pump().catch(() => {});
  };

  const stop = () => {
    if (stopped) return;
    stopped = true;
    video.removeEventListener('timeupdate', onProgress);
    video.removeEventListener('seeking', onProgress);
    video.removeEventListener('seeked', onProgress);
    try {
      URL.revokeObjectURL(objectUrl);
    } catch {
      // 已經釋放
    }
  };

  const fail = (err) => {
    if (failure) return;
    failure = err;
    stop();
    rejectReady(err);
    rejectDone(err);
  };

  const fetchBytes = async (uri) => {
    const response = await apiRaw(uri);
    if (!response.ok) throw new Error(`${uri} -> ${response.status}`);
    return response.arrayBuffer();
  };

  /**
   * 等 SourceBuffer 真的閒下來。
   *
   * <para>
   * `appendBuffer` 與 `remove` 都要求 `updating === false`，否則瀏覽器擲
   * InvalidStateError 而畫面上只看到一行訊息、畫面不再更新（`fail()` 會把整條管線關掉）。
   * `timestampOffset` 的例外更囉嗦——連 `updating` 已經是 false 都可能設不進去，
   * 由 `append` 自己處理。
   * </para>
   */
  const idleNow = () =>
    (buffer.updating ? whenBufferIdle(buffer, t('state.appendFailed')) : Promise.resolve());

  const append = async (bytes, offsetSeconds) => {
    if (stopped) return;
    await idleNow();
    if (offsetSeconds !== null) {
      // 實測：append 排到一半時 append state 是 PARSING_MEDIA_SEGMENT，而 `updating` 已經是
      // false——這時連 timestampOffset 都不能設，等幾個 macrotask 也一樣，只有 abort() 能把
      // 它推回 WAITING_FOR_SEGMENT。撞到就 abort 後重試，否則拖到還沒抓過的段就停在錯誤訊息。
      for (let attempt = 1; ; attempt++) {
        try {
          buffer.timestampOffset = offsetSeconds;
          break;
        } catch (err) {
          if (attempt >= 3) throw err;
          if (buffer.updating) await idleNow();
          else buffer.abort();
        }
      }
    }
    const idle = whenBufferIdle(buffer, t('state.appendFailed'));
    buffer.appendBuffer(bytes);
    await idle;
  };

  const remove = async (range) => {
    if (stopped) return;
    await idleNow();
    const idle = whenBufferIdle(buffer, t('state.appendFailed'));
    buffer.remove(range.start, range.end);
    await idle;
  };

  /**
   * 把第 index 段接到媒體時間軸上。
   *
   * 位置取清單的累計起點（EXTINF）、段內時間戳不從 0 開始時整個平移；解析不出跨度就
   * 當成從 0 開始。任何一段都能單獨接上去，往回拖或跳段都不必重算偏移量（重算等於把
   * 整份清單重新解析一遍）。
   */
  const appendSegment = async (index) => {
    const segment = playlist.segments[index];
    const bytes = await fetchBytes(segment.uri);
    if (stopped) return;
    const span = parseFmp4VideoSpan(tracks, bytes);
    // 接縫只看**前一段**的結尾（不是整條時間軸的最大值）：往回拖之後重新接第 3 段時，
    // 時間軸最大值還在第 27 段的尾端，用最大值會讓 `playbackSeamStart` 永遠走「照清單定位」
    // 那條路，於是在每個接縫又留下那 0.6–12 秒的洞。
    const previousEnd = Number(timeline[index - 1]?.mediaEnd) || 0;
    const mediaStart = playbackSeamStart(mediaStarts[index], previousEnd);
    const mediaDuration = span
      ? span.end - span.start
      : (Number.isFinite(segment.duration) && segment.duration > 0 ? segment.duration : 0);
    await append(bytes, mediaStart - (span ? span.start : 0));
    timeline[index] = {
      index,
      mediaStart,
      mediaEnd: mediaStart + mediaDuration,
      wallStartMs: Number.isFinite(segment.start) ? segment.start : null,
      wallDurS: Number.isFinite(segment.duration) ? segment.duration : null,
    };
    options.onTimeline?.(timeline);
  };

  /** 補資料（必要時先淘汰舊資料）並維護 media.duration。序列化：同時間只有一條管線。 */
  const pump = async () => {
    if (pumping || stopped || failure) return;
    pumping = true;
    try {
      const now = Number(video.currentTime) || 0;

      // 播放頭不在已緩衝的範圍裡（拖到還沒抓過的時段、或往回拖進已淘汰的區間）：直接跳到
      // 播放頭所在的那一段並補上它。舊寫法從讀取頭一段一段往前補，3 小時的窗補到第 10 次
      // append 時 SourceBuffer 就卡在 PARSING_MEDIA_SEGMENT；往回拖更糟——最後一段在播放頭
      // 後面 80 分鐘，「離尾端多近」永遠是「還很遠」，於是什麼都不補，畫面不動而讀數還停在
      // 舊位置（實機回報「往回拖沒反應」）。
      //
      // 「這一段的資料還在緩衝區裡」時不跳：那是播放頭跑到整條時間軸尾端的情況（估算的
      // duration 比媒體結尾多一個暖機量級），跳了只會每一輪重新 append 最後一段；
      // 那時該做的是下面把 duration 收斂到真實結尾。
      const ranges = readBufferedRanges(buffer);
      const covered = ranges.some((range) => now >= range.start - 0.25 && now < range.end);
      const target = playlistSegmentIndexAt(playlist.segments, now);
      if (
        !covered &&
        target !== null &&
        !playbackBufferedFor(ranges, timeline[target])
      ) {
        // 往回拖之後，先前跳過的那一大段還留在緩衝區裡：遠方的資料清掉，否則在同一個窗
        // 來回拖幾次就會把整份清單留在 SourceBuffer 裡（「The SourceBuffer is full」）。
        for (const range of playbackRangesAheadToEvict(ranges, now)) {
          await remove(range);
        }
        nextIndex = target;
        await appendSegment(target);
        if (stopped) return;
        nextIndex = target + 1;
        if (!appendedAny) {
          appendedAny = true;
          resolveReady();
        }
      }

      while (nextIndex < playlist.segments.length) {
        const ranges = readBufferedRanges(buffer);
        if (!playbackNeedsSegment(ranges, now)) break;
        // 配額是固定的，所以每一段 append 前都先丟掉播放頭後方的舊資料。
        for (const range of playbackRangesToEvict(ranges, now)) {
          await remove(range);
        }
        const index = nextIndex;
        await appendSegment(index);
        if (stopped) return;
        nextIndex = index + 1;
        if (!appendedAny) {
          appendedAny = true;
          resolveReady();
        }
      }

      // 全段都補完時把總長度收斂成真正的媒體結尾：估算值（EXTINF 總和）比實際接上的
      // 媒體長度多一個連線暖機量級，留著估算值會讓最後幾秒「怎麼按都不動」。
      if (nextIndex >= playlist.segments.length && media.readyState === 'open' && !buffer.updating) {
        const last = timeline.filter(Boolean).pop();
        if (last) {
          try {
            media.duration = last.mediaEnd;
          } catch {
            // 舊版 Safari 不給設 duration；維持估算值即可。
          }
        }
        resolveDone();
      }
    } catch (err) {
      fail(err);
    } finally {
      pumping = false;
    }
  };

  const initBytes = playlist.initUri ? await fetchBytes(playlist.initUri) : null;
  if (initBytes) await append(initBytes, null);
  tracks = parseFmp4InitTracks(initBytes);

  // 清單的牆鐘總長度先當成播放長度：操作員一拖游標就能跳到還沒抓過的段，
  // 由 seeking 觸發的補資料把內容補上（整份清單一次抓完正是爆配額的原因）。
  const estimated = playlist.segments.reduce(
    (sum, segment) =>
      sum + (Number.isFinite(segment.duration) && segment.duration > 0 ? segment.duration : 0),
    0,
  );
  if (estimated > 0) {
    try {
      media.duration = estimated;
    } catch {
      // 同上：設定不了就讓瀏覽器自己算。
    }
  }

  video.addEventListener('timeupdate', onProgress);
  video.addEventListener('seeking', onProgress);
  video.addEventListener('seeked', onProgress);

  const player = { timeline, ready, done, stop };
  await pump();
  return player;
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

/**
 * 回放工具列的跳躍只動 video.currentTime，不碰 MediaSource 的補段管線。
 * MSE 的 duration 可能是 Infinity，上界改用 seekable 的最後一段，否則 +30 秒
 * 會把游標設到一個不存在的位置並讓播放器停在空白畫面。
 */
function playbackSeekTarget(delta) {
  const video = $('play-video');
  if (!video || !Number.isFinite(video.currentTime)) return null;
  let upper = video.duration;
  if (!Number.isFinite(upper) && video.seekable?.length > 0) {
    upper = video.seekable.end(video.seekable.length - 1);
  }
  if (!Number.isFinite(upper)) upper = video.currentTime;
  return Math.min(Math.max(video.currentTime + delta, 0), upper);
}

function seekPlayback(delta) {
  const target = playbackSeekTarget(delta);
  if (target !== null) $('play-video').currentTime = target;
}

/** 下一張：先暫停再前進一格（HLS 無逐幀索引，用 1/30 秒做一個小步）。 */
function stepPlaybackFrame() {
  const video = $('play-video');
  if (!video) return;
  video.pause();
  const target = playbackSeekTarget(1 / 30);
  if (target !== null) video.currentTime = target;
}

function togglePlaybackFullscreen() {
  const video = $('play-video');
  if (!video) return;
  if (document.fullscreenElement) {
    document.exitFullscreen?.();
  } else {
    video.requestFullscreen?.();
  }
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

    // 舊時段的管線要先停，否則兩條 append 會互搶同一個 video／SourceBuffer。
    activePlayback?.stop();
    activePlayback = null;

    const player = await attachPlaylist($('play-video'), playlist, {
      onTimeline: (timeline) => {
        playClock = buildRecordingClock(timeline);
        renderPlayClock();
      },
    });
    activePlayback = player;
    // 之後的補段與淘汰都在背景進行，err 由 done 回報；這裡只等第一段真的可播。
    player.done.catch((err) => {
      if (activePlayback === player) msg.textContent = err.message;
    });
    await player.ready;
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

/** 事件頁一進來就顯示與原本搜尋相同的「最近 24 小時」，操作員不必先猜要填哪個格式。 */
function defaultEventWindow() {
  const now = new Date();
  const from = new Date(now.getTime() - 24 * 3600 * 1000);
  $('event-from').value = toLocalInputValue(from);
  $('event-to').value = toLocalInputValue(now);
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

/**
 * PTZ 命令一次只送一道，下一道等前一道回應（參考機 web 面板的 queue + lock）。
 *
 * 這不是為了省流量，是為了**順序**：方向鍵放開時送出的是 Stop，而 Stop 與先前的 Move 是兩個
 * 獨立的 HTTP 請求；相機處理 SOAP 慢的時候，Stop 有可能先到（或兩者交錯），鏡頭就會在操作員
 * 放手之後繼續轉——實機症狀是「放開還會動一下」。序列化之後，相機看到的順序與操作員的手一致。
 *
 * 佇列只保留「最新的一道」：連續拖曳時中間那些已經過時的方向沒有送出的價值。
 */
let ptzQueue = Promise.resolve();
let ptzPending = 0;

function ptzEnqueue(task) {
  ptzPending++;
  const run = ptzQueue.then(task, task).finally(() => {
    ptzPending--;
  });
  ptzQueue = run.catch(() => {});
  return run;
}

/** 速度級別（1–8）：參考機的面板是 1–8 滑桿，每一道命令都帶它；我們換算成 ONVIF 的速度向量。 */
function ptzSpeedLevel() {
  const raw = Number($('ptz-speed')?.value);
  return Number.isFinite(raw) ? Math.min(8, Math.max(1, raw)) : 8;
}

/** 速度級別 → ONVIF 速度（-1..1）：8＝全速（與調整前相同），1＝最慢。 */
function ptzVelocityScale() {
  return ptzSpeedLevel() / 8;
}

function setPtzSpeed(level) {
  const next = Math.min(8, Math.max(1, level));
  const slider = $('ptz-speed');
  if (slider) slider.value = String(next);
  const readout = $('ptz-speed-value');
  if (readout) readout.textContent = String(next);
  localStorage.setItem('helivms.ptzSpeed', String(next));
}

async function ptzMove(pan, tilt, zoom) {
  if (!ptzProfileToken) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const scale = ptzVelocityScale();
  // 只有非零軸才縮放：0 代表「這一軸不動」，乘上比例還是 0，不必特別處理但寫出來較清楚。
  const body = {
    profileToken: ptzProfileToken,
    pan: pan * scale,
    tilt: tilt * scale,
    zoom: zoom * scale,
  };
  await ptzEnqueue(async () => {
    const resp = await apiRaw(`/api/ptz/${channelId}/move`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body),
    }).catch(() => null);
    await ptzReport(resp);
  });
}

async function ptzStop() {
  if (!ptzProfileToken) return;
  const channelId = Number($('ptz-channel').value);
  await ptzEnqueue(async () => {
    const resp = await apiRaw(`/api/ptz/${channelId}/stop`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ profileToken: ptzProfileToken }),
    }).catch(() => null);
    await ptzReport(resp);
  });
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

/**
 * 把「選取的那一個」預設點的位置更新成目前位置（參考機 PTZ 面板的「儲存位置」）。
 *
 * 與「儲存預設點」的差別在有沒有帶 presetToken：不帶就是新增一個。操作員微調畫面後要更新
 * 原本那一個，否則巡邏／圖控照的還是舊位置，而清單會一直長出新的預設點。
 */
async function updatePtzPreset() {
  const token = $('ptz-preset-list').value;
  if (!ptzProfileToken || !token) {
    $('ptz-msg').textContent = t('ptz.notSupported');
    return;
  }
  const channelId = Number($('ptz-channel').value);
  const selected = $('ptz-preset-list').selectedOptions[0];
  const name = (selected?.textContent ?? '').trim() || token;
  const resp = await apiRaw(`/api/ptz/${channelId}/presets`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ profileToken: ptzProfileToken, name, presetToken: token }),
  }).catch(() => null);
  if (await ptzReport(resp, t('ptz.updatedPreset'))) await loadPtzPresets(channelId);
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

  // Esc 與「分頁切走／視窗失焦」一律停止：滑鼠放開的事件不一定會送到（拖到瀏覽器外面、
  // 切到別的視窗、手機上來電），而鏡頭會一直轉到逾時——這是安全問題，不只是體驗問題。
  document.addEventListener('keydown', (ev) => {
    if (ev.key === 'Escape') void ptzStop();
  });
  document.addEventListener('visibilitychange', () => {
    if (document.hidden) void ptzStop();
  });
  window.addEventListener('blur', () => void ptzStop());

  // 速度：滑桿與慢／快兩顆按鈕（與參考機同一組控制），值存在本機，換頁不會被重置。
  const slider = $('ptz-speed');
  if (slider) {
    const remembered = Number(localStorage.getItem('helivms.ptzSpeed'));
    setPtzSpeed(Number.isFinite(remembered) && remembered > 0 ? remembered : 8);
    slider.addEventListener('input', () => setPtzSpeed(Number(slider.value)));
  }
  $('ptz-slower')?.addEventListener('click', () => setPtzSpeed(ptzSpeedLevel() - 1));
  $('ptz-faster')?.addEventListener('click', () => setPtzSpeed(ptzSpeedLevel() + 1));
  $('ptz-preset-update')?.addEventListener('click', () => void updatePtzPreset());
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
// 事件頁的來源類型下拉只是一層本機過濾：後端 /api/events/search 仍收同一組 q/from/to/limit，
// 不為了「畫面像参考機」另開端點。0 代表全部，其餘是 ForensicSource 的位元值。
const FORENSIC_SOURCE_FILTERS = { all: 0, alarm: 1, access: 2, pos: 4, edge: 8 };

/** 把事件頁的可見時間範圍轉成後端要的 UTC；空值沿用原本「最近 24 小時」的行為。 */
function eventQueryWindow() {
  const now = new Date();
  const fromText = $('event-from')?.value;
  const toText = $('event-to')?.value;
  const from = fromText ? new Date(fromText) : new Date(now.getTime() - 24 * 3600 * 1000);
  const to = toText ? new Date(toText) : now;
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()) || to <= from) {
    $('events-msg').textContent = t('state.needRange');
    return null;
  }
  return { from: from.toISOString(), to: to.toISOString() };
}

async function searchEvents(raw) {
  const q = raw.trim();
  if (!q) {
    $('events-msg').textContent = t('state.needKeyword');
    return;
  }
  const range = eventQueryWindow();
  if (!range) return;
  const limit = 50;
  const sourceFilter = FORENSIC_SOURCE_FILTERS[$('event-type')?.value] ?? 0;
  const params = new URLSearchParams({ q, from: range.from, to: range.to, limit: String(limit) });
  const page = await api(`/api/events/search?${params}`).catch(() => null);
  if (!page) {
    $('events-msg').textContent = t('state.disconnected');
    return;
  }
  const rows = (page.items ?? [])
    .map((hit) => ({ hit, row: eventRow(hit) }))
    .filter(({ row }) => !sourceFilter || (row.source & sourceFilter) !== 0);
  $('events').querySelector('tbody').innerHTML = rows
    .map(({ row }) => {
      const { id, time, source, text, status } = row;
      return `<tr><td>${esc(id ? '#' + id : '')}</td><td>${esc(time)}</td><td>${esc(FORENSIC_SOURCE_NAMES[source]?.() ?? '—')}</td><td>${esc(text || '—')}</td><td>${esc(status || '—')}</td></tr>`;
    })
    .join('');
  if (rows.length === 0) {
    $('events-msg').textContent = t('state.noResults');
  } else if (rows.length >= limit) {
    $('events-msg').textContent = t('search.foundCapped', { total: rows.length, shown: rows.length });
  } else {
    $('events-msg').textContent = t('search.found', { total: rows.length });
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
  $('logout').addEventListener('click', (e) => {
    e.preventDefault();
    logout();
  });

  bindTabs();
  showTab('live');

  $('live-stop').addEventListener('click', stopLiveView);
  releaseLiveSessionsOnUnload();
  // 版面按鈕同時是開播與分頁：不同格數回到第一批，同一格數換下一批。
  // 用按鈕而不是 select，是因為再選一次相同的值不會觸發 change。
  $('live-layout').addEventListener('click', (event) => {
    const btn = event.target.closest('[data-layout]');
    if (!btn) return;
    const next = Number(btn.dataset.layout);
    if (!LIVE_LAYOUTS.includes(next)) return;
    if (next === liveLayout) livePage += 1;
    else {
      liveLayout = next;
      livePage = 0;
    }
    void openLiveView();
  });
  renderLiveGrid();
  // 每格的工具列（全螢幕／截圖／PTZ）：委派給格線容器，重畫後不必重新綁定。
  $('live-grid').addEventListener('click', onLiveGridClick);

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
  // 回放工具列（±30 秒／下一張／全螢幕）：對齊參考機 player 的控制項，全部只動本機 video。
  $('play-back30')?.addEventListener('click', () => seekPlayback(-30));
  $('play-next-frame')?.addEventListener('click', stepPlaybackFrame);
  $('play-forward30')?.addEventListener('click', () => seekPlayback(30));
  $('play-fullscreen')?.addEventListener('click', togglePlaybackFullscreen);
  defaultPlayWindow();
  defaultEventWindow();

  $('search-form').addEventListener('submit', (e) => {
    e.preventDefault();
    searchEvents($('q').value).catch(console.error);
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
  for (const id of ['locale']) {
    const pick = $(id);
    if (pick) pick.value = applied;
  }

  updateChrome();
  await refreshLicense();
  await refreshAll();
  await refreshUsage();
  return applied;
}

function bindLocalePicker() {
  for (const id of ['locale']) {
    const pick = $(id);
    if (!pick) continue;
    pick.value = locale();
    pick.addEventListener('change', () => {
      void applyLocale(pick.value);
    });
  }
}

async function boot() {
  setLocale(storedLocale());
  applyDom(document);
  for (const id of ['locale']) {
    const pick = $(id);
    if (pick) pick.value = locale();
  }

  wire();
  updateChrome();
  bindLocalePicker();

  // 登入頁是完全獨立的 login.html。這裡不再自己畫一個登入框：憑證不可用就直接導向，
  // 避免舊版「主介面亮著、每一支 API 默默回 401」的症狀，也讓兩頁共用一份 transport。
  if (!(await hasAccess())) {
    window.location.replace('login.html');
    return;
  }
  await enterApp();

  // 離開頁面時明確停掉重連。沒有這行的話 pagehide 之後重連計時器還在跑，
  // 會在背景不斷嘗試連線直到分頁被回收。
  window.addEventListener('pagehide', disconnectLive);
  setInterval(() => {
    if ($('app-shell').hidden) return;
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
