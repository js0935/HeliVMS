import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const authSource = readFileSync(join(root, 'auth.js'), 'utf8');
const loginSource = readFileSync(join(root, 'login.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
const loginHtml = readFileSync(join(root, 'login.html'), 'utf8');
const stylesSource = readFileSync(join(root, 'styles.css'), 'utf8');
// M243：介面文字搬到 i18n.js 後，原始碼斷言不能只看 app.js，
// 否則「字串還在不在」會被誤判成翻譯被刪掉。這裡同時確認 key 有被使用、且目錄裡真的有譯文。
const i18nSource = readFileSync(join(root, 'i18n.js'), 'utf8');

/** 斷言某個 key 被 app.js 引用，且目錄中確實有對應譯文。 */
function expectLocalized(source, key, zhTW) {
  expect(source).toContain(`t('${key}')`);
  expect(i18nSource).toContain(`'${key}': '${zhTW}'`);
}

describe('five-tab console', () => {
  it('has dropped the legacy admin/board panels', () => {
    for (const gone of ['renderBoard', 'renderNotifs', 'renderProviders', 'gatedFetch', 'admin-nav', 'central-panel', 'alarm-panel', 'sw-panel']) {
      expect(appSource, gone).not.toContain(gone);
      expect(indexSource, gone).not.toContain(gone);
    }
  });

  it('opens the live tab by default and switches on click', () => {
    expect(appSource).toMatch(/showTab\('live'\);/);
    expect(appSource).toMatch(/btn\.addEventListener\('click', \(\) => showTab\(btn\.dataset\.tab\)\)/);
  });

  it('keeps the single-source boot/poll entry point', () => {
    expect(appSource).toMatch(/^boot\(\);$/m);
    expect(appSource).toMatch(/POLL_MS[\s\S]*setInterval/);
    expect(appSource).toMatch(/pollGate\(document\.activeElement\?\.tagName, document\.hidden\)/);
  });
});

describe('standalone login page', () => {
  it('登入畫面的使用者可見文字都走 i18n，不在 HTML 寫死', () => {
    // 登入頁是獨立畫面，最容易被直接寫死中文/英文；三個 key 都必須在目錄裡有譯文。
    for (const key of ['login.title', 'login.hint', 'login.withKey']) {
      expect(loginHtml, key).toMatch(new RegExp(`data-i18n="${key.replace('.', '\\.')}"`));
      expect(i18nSource, key).toContain(`'${key}':`);
    }
    expectLocalized(loginSource, 'login.needKey', '請輸入 API 金鑰');
  });

  it('登入表單只住在 login.html，index.html 不再保留欄位', () => {
    expect(loginHtml).toMatch(/<form id="login" class="login-fields">/);
    for (const id of ['apikey', 'user', 'pass', 'login', 'login-msg', 'login-key', 'login-locale']) {
      expect(loginHtml, id).toMatch(new RegExp(`id="${id}"`));
      expect(indexSource, `index.html 不該再有 #${id}`).not.toMatch(new RegExp(`id="${id}"`));
    }
  });

  it('登入成功後帶著 token 回到主介面', () => {
    expect(loginSource).toMatch(/setApiKey\(state\.token\)/);
    expect(loginSource).toMatch(/window\.location\.replace\(MAIN_PAGE\)/);
    expect(loginSource).toMatch(/const MAIN_PAGE = 'index\.html'/);
  });
});

describe('api-key flow', () => {
  it('validates the API key on the standalone login page before redirecting', () => {
    // 金鑰改在獨立登入頁輸入，Enter 不再只是 refreshAll()（那會在主介面還藏著時
    // 默默刷新一堆 401），而是走 enterWithKey() 先驗憑證再切畫面。
    expect(loginSource).toMatch(/apikey'\)\.addEventListener\('keydown'[\s\S]*Enter[\s\S]*enterWithKey\(\)/);
    expect(loginSource).toMatch(/login-key'\)\.addEventListener\('click'/);
    expect(loginSource).toMatch(/async function enterWithKey\(\)/);
    expect(loginSource).toMatch(/if \(await hasAccess\(\)\)[\s\S]*window\.location\.replace\(MAIN_PAGE\)/);
  });

  it('只有在憑證可用時才啟動主介面與背景輪詢', () => {
    expect(appSource).toMatch(/if \(!\(await hasAccess\(\)\)\) \{[\s\S]*window\.location\.replace\('login\.html'\)[\s\S]*return;/);
    expect(appSource).toMatch(/if \(\$\('app-shell'\)\.hidden\) return;/);
    // 只檢查 localStorage 有沒有值，會放過伺服器重開後已失效的舊 token，
    // 症狀就是「主介面出現但每支 API 都 401」。必須用 /api/config 真實驗證。
    expect(authSource).toMatch(/export async function hasAccess\(\)[\s\S]*apiRaw\('\/api\/config'\)[\s\S]*return res\.ok/);
  });
});

describe('search feedback', () => {
  it('reports empty result sets and a capped page', () => {
    expectLocalized(appSource, 'state.noResults', '沒有符合的結果');
    // 後端只回當頁筆數（count），沒有總數；結果剛好塞滿一頁時改用「前 N 筆」提示，
    // 不再讀一個不存在的 page.total（原症狀是畫面顯示「找到 undefined 筆」）。
    expect(appSource).toMatch(/search\.foundCapped/);
    expect(appSource).not.toMatch(/page\.total/);
  });

  it('guards empty queries with a hint', () => {
    expect(appSource).toMatch(/if \(!q\)[\s\S]*t\('state\.needKeyword'\)/);
    expect(i18nSource).toContain("'state.needKeyword': '請輸入關鍵字'");
  });

  it('adds a live region under the search table', () => {
    expect(indexSource).toMatch(/id="events-msg"[^>]*aria-live="polite"/);
  });
});

describe('event search table', () => {
  it('labels the five columns it renders', () => {
    for (const col of ['time', 'source', 'text', 'status']) {
      expect(indexSource).toMatch(new RegExp(`<th scope="col"[^>]*data-i18n="col\\.${col}"`));
    }
  });

  it('renders per-hit source names', () => {
    // 翻譯必須在呼叫時求值，否則切換語言後這張來源表會停在舊語言。
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES = \{ 1: \(\) => t\('replay\.sourceAlarm'\), 2: \(\) => t\('replay\.source'\), 4: \(\) => 'POS', 8: \(\) => t\('replay\.sourceEdgeAi'\) \}/);
    expect(appSource).toMatch(/FORENSIC_SOURCE_NAMES\[source\]\?\.\(\)/);
  });

  it('shows id and status, and leaves the table read-only', () => {
    expect(appSource).toMatch(/esc\(status \|\| '—'\)/);
    // 事件紀錄是唯讀檢視：不再有確認／誤報的列內動作，也不再打 alarm-board。
    expect(appSource).not.toMatch(/data-sack=/);
    expect(appSource).not.toMatch(/data-sfa=/);
    expect(appSource).not.toMatch(/\/api\/alarm-board/);
  });
});

describe('connection banner', () => {
  it('adds a live status element in the top bar', () => {
    expect(indexSource).toMatch(/id="conn"[^>]*role="status"[^>]*aria-live="polite"/);
  });

  it('flips the banner inside the shared transport around fetch', () => {
    expect(authSource).toMatch(/const response = await fetch\(path, \{ \.\.\.init, headers \}\);\s*setConn\(true\);/);
    expect(authSource).toMatch(/catch \(err\) \{\s*setConn\(false\);\s*throw err;\s*\}/);
  });

  it('renders a disconnect label and hides empty state', () => {
    expect(authSource).toMatch(/el\.textContent = ok \? '' : t\('state\.disconnected'\);/);
    expect(i18nSource).toContain("'state.disconnected': '連線中斷'");
  });
});

describe('license banner', () => {
  it('adds a live status element beside the connection badge', () => {
    expect(indexSource).toMatch(/id="lic"[^>]*role="status"[^>]*aria-live="polite"/);
  });

  it('polls license state alongside the rest of the console', () => {
    // 只在 boot 取一次不夠：授權可能在執行中被匯入或到期。
    expect(appSource).toMatch(/await refreshLicense\(\);\s*await refreshAll\(\);/);
    expect(appSource).toMatch(/setInterval\(\(\) => \{[\s\S]*refreshLicense\(\);[\s\S]*refreshAll\(\);/);
  });

  it('names the missing features instead of leaving an empty badge', () => {
    expect(appSource).toMatch(/apiRaw\('\/api\/license'\)/);
    expect(appSource).toMatch(/t\('state\.unauthorized'/);
    expect(i18nSource).toContain("'state.unauthorized': '未授權：{reason}'");
    expect(appSource).toMatch(/\.filter\(\(f\) => !f\.allowed\)/);
  });

  it('never inserts license text as HTML', () => {
    const fn = appSource.slice(
      appSource.indexOf('async function refreshLicense'),
      appSource.indexOf('async function refreshAll'),
    );
    expect(fn).not.toMatch(/innerHTML/);
  });

  it('reuses the server-side expiry wording instead of rewriting it', () => {
    const fn = appSource.slice(
      appSource.indexOf('async function refreshLicense'),
      appSource.indexOf('async function refreshAll'),
    );
    expect(fn).toMatch(/status\.expiryMessage/);
    expect(fn).not.toMatch(/剩 \$\{/);
  });
});

describe('live streaming (WHEP)', () => {
  it('opens the viewer through the injected transport', () => {
    expect(appSource).toMatch(/createLiveViewer\(\{ apiRaw, t \}\)/);
    expect(appSource).toMatch(/viewer\.open\(channelId, video/);
    expect(appSource).toMatch(/\$\('live-layout'\)\.addEventListener\('click', \(event\) =>/);
    // 沒有「開啟」按鈕，進到主介面就先開目前版面第一批，不能留一格空畫面。
    expect(appSource).toMatch(/if \(!alertSocket\) connectLive\(\);[\s\S]{0,200}void openLiveView\(\);/);
  });
});

describe('snapshot', () => {
  it('captures the decoded frame to a PNG download', () => {
    // 直接複製畫面上已解碼的那一格：零延遲、零後端相依。
    expect(appSource).toMatch(/getContext\('2d'\)\.drawImage\(video, 0, 0\)/);
    expect(appSource).toMatch(/canvas\.toBlob\(/);
    expect(appSource).toMatch(/link\.download = `snapshot-ch/);
    expect(appSource).toMatch(/URL\.createObjectURL\(blob\)/);
  });

  it('reports success and failure through the message region', () => {
    expectLocalized(appSource, 'state.snapshotDone', '已下載截圖');
    expectLocalized(appSource, 'state.snapshotFailed', '截圖失敗');
    expect(indexSource).toMatch(/id="snap-capture"[^>]*data-i18n="action\.capture"/);
  });
});

describe('PTZ (M310 /api/ptz)', () => {
  it('lists device profiles through the authenticated transport', () => {
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/profiles`\)/);
    expect(appSource).toMatch(/ptzProfileToken = profiles\[0\]\.token/);
  });

  it('sends continuous move, stop and home with the profile token', () => {
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/move`, \{/);
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/stop`, \{/);
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/home`, \{/);
    // 速度級別換算成 ONVIF 的速度向量（預設 1＝最小幅度）；三軸一起送。
    expect(appSource).toMatch(/pan: pan \* scale,/);
    expect(appSource).toMatch(/tilt: tilt \* scale,/);
    expect(appSource).toMatch(/zoom: zoom \* scale,/);
  });

  it('serialises PTZ commands so a late Stop cannot overtake the Move', () => {
    // 參考機 web 面板把 PTZ 命令排成一條佇列（一次只有一道在飛）。少了它，放開方向鍵時
    // 送出的 Stop 可能比先前的 Move 先到，鏡頭就在放手之後繼續轉。
    expect(appSource).toMatch(/function ptzEnqueue\(task\)/);
    expect(appSource).toMatch(/await ptzEnqueue\(async \(\) => \{/);
    expect(appSource).toContain('let ptzQueue = Promise.resolve();');
  });

  it('stops the camera when the operator leaves the panel or presses Esc', () => {
    // 滑鼠放開的事件不一定送得到（拖到視窗外、切換視窗、手機上來電），鏡頭會轉到逾時。
    expect(appSource).toMatch(/ev\.key === 'Escape'/);
    expect(appSource).toMatch(/document\.hidden/);
    expect(appSource).toMatch(/window\.addEventListener\('blur'/);
    // 懸浮控制也要一起停：從監看牆切走時，那台相機不能還在轉。
    expect(appSource).toMatch(/document\.addEventListener\('pointerup', stopAllPtz\)/);
    expect(appSource).toMatch(/function stopAllPtz\(\)[\s\S]{0,200}if \(activeFloatingPtz\) void ptzStop\(activeFloatingPtz\.target\)/);
  });

  it('offers the speed control of the reference panel and remembers it', () => {
    expect(indexSource).toMatch(/id="ptz-speed"[^>]*type="range"[^>]*min="1"[^>]*max="8"[^>]*value="1"/);
    expect(indexSource).toMatch(/id="ptz-speed-value"[^>]*>1</);
    expect(appSource).toMatch(/function ptzSpeedLevel\(\)[\s\S]{0,200}: 1;/);
    expect(appSource).toMatch(/function ptzVelocityScale\(\)/);
    // 速度向量壓到原本的 1/10：單靠短脈衝不夠，SOAP 往返仍會把幅度拉長。
    expect(appSource).toMatch(/return ptzSpeedLevel\(\) \/ 80;/);
    expect(appSource).toContain("localStorage.setItem('helivms.ptzSpeed'");
    // 懸浮控制與 PTZ 頁共用速度：沒有記住過時必須是最小檔，不能退回全速 8。
    expect(appSource).toMatch(/remembered : 1\)/);
    for (const key of ['ptz.speed', 'ptz.slower', 'ptz.faster', 'ptz.updatePreset']) {
      expect(i18nSource, key).toContain(`'${key}':`);
    }
  });

  it('updates the selected preset position instead of always adding one', () => {
    // 參考機的「儲存位置」：帶 presetToken 的 SetPreset 把那個預設點移到目前位置。
    expect(indexSource).toMatch(/id="ptz-preset-update"[^>]*data-i18n="ptz\.updatePreset"/);
    expect(appSource).toMatch(/presetToken: token \}/);
    expect(appSource).toMatch(/async function updatePtzPreset\(\)/);
  });

  it('manages presets: list, save, goto, delete', () => {
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/presets\?profileToken=/);
    expect(appSource).toMatch(/apiRaw\(`\/api\/ptz\/\$\{channelId\}\/presets`, \{/);
    expect(appSource).toMatch(/\/presets\/\$\{encodeURIComponent\(token\)\}\/goto/);
    expect(appSource).toMatch(/\/presets\/\$\{encodeURIComponent\(token\)\}\?profileToken=/);
  });

  it('distinguishes "not supported" (409) from a generic connect failure', () => {
    expect(appSource).toMatch(/resp\.status === 409/);
    expectLocalized(appSource, 'ptz.notSupported', '此設備不支援 PTZ');
    expect(appSource).toContain("t('ptz.connectFailed'");
    expect(i18nSource).toContain("'ptz.connectFailed': '");
  });

  it('moves one small step per click and stops itself', () => {
    // ContinuousMove 是速度命令；點一下送固定脈衝，Move 與 Stop 都進同一條佇列，
    // 幅度才不會被按住時間或 SOAP 往返拉長。
    expect(appSource).toMatch(/const PTZ_PULSE_MS = 80;/);
    expect(appSource).toMatch(/function ptzPulse\(pan, tilt, zoom, target\) \{[\s\S]{0,200}window\.setTimeout\(\(\) => void ptzStop\(target\), PTZ_PULSE_MS\)/);
    expect(appSource).toMatch(/ptzPulse\(pan, tilt, zoom, ptzPageTarget\(\)\)/);
    // 不再用 keyup 當停止點：脈衝自己會送 Stop，放開只是安全網。
    expect(appSource).not.toMatch(/btn\.addEventListener\('keyup', \(\) => void ptzStop\(\)\)/);
    expect(appSource).toMatch(/\$\('ptz-stop'\)\.addEventListener\('click', \(\) => void ptzStop\(\)\)/);
  });

  it('refuses to move without a connected profile', () => {
    // 只 pin 在 ptzMove 函式開頭：其他預設點函式也有「不支援」的檢查，
    // 用跨函式的萬用字元會在 ptzMove 的守門被刪掉後仍然通過。
    expect(appSource).toMatch(/async function ptzMove\(pan, tilt, zoom, target = ptzPageTarget\(\)\) \{\s*if \(!target\.token\) \{\s*target\.msg\.textContent = t\('ptz\.notSupported'\);/);
    expect(appSource).toMatch(/function ptzPageTarget\(\) \{[\s\S]{0,160}token: ptzProfileToken/);
  });
});

describe('floating PTZ on the live wall', () => {
  it('toggles a pad over the clicked cell instead of leaving the picture', () => {
    // ⌖ 不切分頁、不開第二條 WHEP：就在那一格上疊控制，並把該格頻道帶過去。
    expect(appSource).toMatch(/void toggleCellPtz\(cell, channelId\)/);
    expect(appSource).toMatch(/async function toggleCellPtz\(cell, channelId\)/);
    expect(appSource).toMatch(/activeFloatingPtz = \{ cell, target: \{ channelId, token: '', msg \} \}/);
    expect(appSource).toMatch(/overlay\.hidden = false/);
    expect(appSource).not.toContain('openCellTool(');
  });

  it('caches one profile per channel and shares the lookup with the PTZ page', () => {
    expect(appSource).toMatch(/const floatingPtzProfiles = new Map\(\)/);
    expect(appSource).toMatch(/async function ensureFloatingPtz\(channelId, msg\)/);
    expect(appSource).toMatch(/const profiles = await resolvePtzProfiles\(channelId, msg\)/);
    expect(appSource).toMatch(/floatingPtzProfiles\.set\(channelId, token\)/);
    expect(appSource).toMatch(/async function resolvePtzProfiles\(channelId, msg\)/);
    // PTZ 頁也走同一個 409／空清單判斷，不能讓兩邊對「不支援」有不一樣的說法。
    expect(appSource).toMatch(/const profiles = await resolvePtzProfiles\(channelId, msg\);\s*if \(!profiles\) return;/);
  });

  it('scopes pad commands to the open cell and the same serial queue', () => {
    expect(appSource).toMatch(/function floatingTarget\(event\)[\s\S]{0,320}btn\.closest\('\.live-cell'\) !== activeFloatingPtz\.cell\) return null/);
    expect(appSource).toMatch(/ptzPulse\(pan, tilt, zoom, target\)/);
    expect(appSource).toMatch(/void ptzStop\(activeFloatingPtz\.target\)/);
    expect(appSource).toMatch(/void ptzHome\(activeFloatingPtz\.target\)/);
    // 浮動控制用獨立的 data-ptz-move，不能被 bindPtzPad 的全域綁定攔去控制 PTZ 頁那台。
    expect(appSource).toMatch(/data-ptz-move/);
    expect(appSource).toMatch(/liveGrid\.addEventListener\('pointerdown', onFloatingPtzDown\)/);
  });

  it('issues Stop when the pad is closed or the live view is torn down', () => {
    // ContinuousMove 是速度命令；只把 overlay 藏起來，鏡頭會一直轉到逾時。
    expect(appSource).toMatch(/if \(active\.target\.token\) void ptzStop\(active\.target\)/);
    expect(appSource).toMatch(/closeFloatingPtz\(\);\s*const count = liveLayoutCount\(\)/);
    expect(appSource).toMatch(/function stopLiveView\(\{ external = true \} = \{\}\) \{[\s\S]{0,180}closeFloatingPtz\(\)/);
    expect(appSource).toMatch(/if \(event\.persisted\) return;\s*closeFloatingPtz\(\)/);
  });

  it('translates the overlay title and close button in both catalogs', () => {
    for (const key of ['ptz.floating', 'ptz.close', 'ptz.stop', 'ptz.home']) {
      expect(i18nSource, key).toContain(`'${key}':`);
    }
  });
});

describe('remote playback (M239 HLS)', () => {
  it('exposes a playback panel with an accessible form and a video element', () => {
    // data-i18n 必須在只包文字的 span 上，不能在含 <video>／計數 span 的 <h2> 上，
    // 否則 applyDom 的 textContent 會刪掉那些子節點。
    expect(indexSource).toMatch(/<h2[^>]*>\s*<span data-i18n="nav\.remotePlayback">/);
    expect(indexSource).toMatch(/<video id="play-video"[^>]*controls[^>]*playsinline/);
    expect(indexSource).toMatch(/id="play-msg"[^>]*aria-live="polite"/);
    // 標籤文字走目錄，屬性用 data-i18n-attr（英文字面在原始碼裡，中文譯文在目錄）。
    for (const id of ['play-node', 'play-channel', 'play-from', 'play-to']) {
      const tag = indexSource.match(new RegExp(`<[^>]+id="${id}"[^>]*>`))?.[0] ?? '';
      expect(tag, `#${id}`).toMatch(/aria-label=/);
      expect(tag, `#${id}`).toMatch(/data-i18n-attr=/);
    }
  });

  it('fetches the playlist through the single authenticated transport', () => {
    // M301：來源節點選「本機」走本機清單，選子節點走跨機代理；兩者都經 apiRaw。
    expect(appSource).toMatch(/buildPlaylistQuery\(\{ channelId, stream: 'main', from, to \}\)/);
    expect(appSource).toMatch(/buildCentralPlaylistQuery\(\{ nodeId, channelId, stream: 'main', from, to \}\)/);
    expect(appSource).toMatch(/await apiRaw\(/);
    expect(appSource).toMatch(/const playlist = parseM3u8\(await response\.text\(\)\)/);
  });

  it('pushes the init segment and every media segment through the authenticated transport', () => {
    expect(appSource).toMatch(/if \(initBytes\) await append\(initBytes, null\)/);
    expect(appSource).toMatch(/const bytes = await fetchBytes\(segment\.uri\)/);
    expect(appSource).toMatch(/await append\(bytes, mediaStart - \(span \? span\.start : 0\)\)/);
    expect(appSource).toMatch(/const response = await apiRaw\(uri\)/);
    expect(appSource).toMatch(/const player = \{ timeline, ready, done, stop \}/);
  });

  it('offers a ready/done handle so the page can play before the whole window is fetched', () => {
    // 舊版 await 整份清單 append 完才回傳：40 分鐘的窗會撞上 SourceBuffer 配額，
    // 畫面黑屏、訊息停在「載入中」，而即時監看完全正常。
    expect(appSource).toMatch(/resolveReady\(\)/);
    expect(appSource).toMatch(/await player\.ready/);
    expect(appSource).toMatch(/player\.done\.catch/);
  });

  it('stitches segments onto one media timeline instead of restarting at zero', () => {
    // 每個錄影檔的時間戳都從 0 重新開始：不設 timestampOffset 直接 append，後段的幀
    // 會覆蓋到前段的時間軸上，操作員實際只能看第一段，而且 log 乾淨、沒有任何報錯。
    expect(appSource).toMatch(/buffer\.timestampOffset = offsetSeconds/);
    expect(appSource).toMatch(/parseFmp4InitTracks\(initBytes\)/);
    expect(appSource).toMatch(/parseFmp4VideoSpan\(tracks, bytes\)/);
    // 位置取清單 EXTINF 的累計值，任何一段都能單獨接上去；接縫的小洞從上一段結尾補起，
    // 舊索引的牆鐘 EXTINF 才不會在每段之間留下沒有資料的洞（播到洞邊就停住）。
    expect(appSource).toMatch(/const mediaStarts = playlistMediaStarts\(playlist\.segments\)/);
    expect(appSource).toMatch(/playbackSeamStart\(mediaStarts\[index\], previousEnd\)/);
    expect(appSource).toMatch(/await append\(bytes, mediaStart - \(span \? span\.start : 0\)\)/);
  });

  it('邊播邊補並淘汰舊緩衝，不會一次抓完整份清單', () => {
    // 實機症狀：40 分鐘的窗得到「The SourceBuffer is full, and cannot free space to
    // append additional buffers」，畫面黑屏、訊息停在載入中，即時監看卻完全正常。
    // 兩個關鍵呼叫：補段前先看播放頭是否接近緩衝尾端、append 前先 remove 舊資料。
    expect(appSource).toMatch(/if \(!playbackNeedsSegment\(ranges, now\)\) break/);
    expect(appSource).toMatch(/playbackRangesToEvict\(ranges, now\)/);
    expect(appSource).toMatch(/await remove\(range\)/);
    // endOfStream 之後就不能再 appendBuffer（往回拖要重抓），所以這條管線不能收尾。
    const body = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(body).not.toMatch(/media\.endOfStream\(\)/);
  });

  it('只在緩衝區閒著時改 timestampOffset 或 append／remove', () => {
    // 實機症狀（3 小時的窗拖到 80%）：訊息變成「Failed to set the 'timestampOffset'
    // property on 'SourceBuffer': The timestamp offset may not be set while the
    // SourceBuffer's append state is 'PARSING_MEDIA_SEGMENT'」，畫面停在原地不再更新。
    // 上一次 append 還沒 updateend 就改 timestampOffset／再 appendBuffer／remove，
    // 瀏覽器直接擲 InvalidStateError，而畫面上只有一行看起來像「緩衝區滿了」的訊息。
    const body = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(body).toMatch(/const idleNow = \(\) =>/);
    expect(body).toMatch(/await idleNow\(\);\s*\n\s*if \(offsetSeconds !== null\) \{/);
    expect(body).toMatch(/await idleNow\(\);\s*\n\s*const idle = whenBufferIdle\(/);
  });

  it('append 狀態卡住時用 abort() 解開再重試，不是等它自己好', () => {
    // 實測：卡住時 `updating` 已經是 false，append state 卻還是 PARSING_MEDIA_SEGMENT，
    // 等 4 個 macrotask（含 100ms）重試都仍然擲同一個例外；只有 buffer.abort() 能把它
    // 推回 WAITING_FOR_SEGMENT。沒有這條路，拖到沒抓過的時段就永遠停在錯誤訊息上。
    const body = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(body).toMatch(/buffer\.abort\(\)/);
    expect(body).toMatch(/if \(attempt >= 3\) throw err/);
  });

  it('拖到還沒抓過的時段會直接跳到那一段，不從讀取頭逐段補', () => {
    // 3 小時窗＝24 段、約 30 MB。逐段往前補等於把整份清單抓一遍，而且第 10 次 append
    // 就讓 SourceBuffer 卡死；播放頭不在緩衝範圍內時應直接跳到它所在的段並補上它。
    const body = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(body).toMatch(/const covered = ranges\.some\(/);
    expect(body).toMatch(/playlistSegmentIndexAt\(playlist\.segments, now\)/);
    expect(body).toMatch(/if \(\s*!covered &&\s*target !== null &&\s*!playbackBufferedFor\(ranges, timeline\[target\]\)\s*\) \{/);
  });

  it('往回拖到已淘汰的時段時會重抓那一段，不是什麼都不補', () => {
    // 實機症狀（3 小時窗播到 80% 後拖回 10%）：t 變成 974、paused=false，但緩衝區仍是
    // [7770, 8113]，畫面與時鐘都停在 23:15，看起來完全沒反應。根因有兩個：
    // ①`playbackNeedsSegment` 只看「離最後一段結尾多近」，最後一段在播放頭後面 80 分鐘，
    //   判斷永遠是 false；②沒把播放頭前方很遠的緩衝丟掉，來回拖幾次就撞上 SourceBuffer 配額。
    const body = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(body).toMatch(/await appendSegment\(target\)/);
    expect(body).toMatch(/playbackRangesAheadToEvict\(ranges, now\)/);
  });

  it('shows the recording wall-clock time for the current position', () => {
    // 操作員要確認「畫面這一刻是什麼時候錄的」；原生進度條只有媒體相對秒數，
    // 回答不了這個問題。
    expect(indexSource).toMatch(/id="play-clock"/);
    expect(appSource).toMatch(/\$\('play-video'\)\.addEventListener\('timeupdate', renderPlayClock\)/);
    expect(appSource).toMatch(/\$\('play-video'\)\.addEventListener\('seeked', renderPlayClock\)/);
    expect(appSource).toMatch(/buildRecordingClock\(timeline\)/);
    expect(appSource).toMatch(/t\('replay\.recordingTime', \{ time: formatLocalClock\(at\) \}\)/);
  });

  it('describes the loaded window as recording times, not as a duration', () => {
    // 「N 段／M 秒」回答不了「這是不是我要的時段」；時段欄位要給錄影的起訖時刻。
    expect(appSource).toMatch(/t\('replay\.playlistWindow'/);
  });

  it('never plays a segment with a bare fetch that would skip the API key', () => {
    const playerBody = appSource.slice(appSource.indexOf('async function attachPlaylist'));
    expect(playerBody).not.toMatch(/[^.\w]fetch\(/);
  });

  it('reports unsupported browsers instead of failing silently', () => {
    expect(appSource).toMatch(/'MediaSource' in window/);
    expect(appSource).toMatch(/msg\.textContent = err\.message/);
  });

  it('wires the form and seeds a one-hour default window', () => {
    expect(appSource).toMatch(/\$\('play-form'\)\.addEventListener\('submit', playRemote\)/);
    expect(appSource).toMatch(/defaultPlayWindow\(\)/);
  });

  it('shows camera names in the playback channel picker', () => {
    // number + datalist 的建議清單在數字輸入框只顯示主鍵（115／116／117），
    // 操作員認不出鏡頭；select 的 option 文字直接用攝影機名稱，value 仍是主鍵。
    expect(indexSource).toMatch(/<select id="play-channel"/);
    expect(indexSource).not.toMatch(/id="play-channel-options"/);
    expect(appSource).toMatch(/label: c\.name/);
    expect(appSource).toMatch(/\$\{esc\(r\.label\)\}/);
    expect(appSource).toMatch(/centralChannelRows\(await api\('\/api\/central\/channels'\)/);
    // 函式必須真的 import 進來：呼叫包在 catch 裡時，ReferenceError 會被吞掉。
    expect(appSource).toMatch(/^\s*centralChannelRows,\s*$/m);
    expect(appSource).toMatch(/\$\('play-node'\)\?\.addEventListener\('change', fillPlayChannels\)/);
  });

  it('reserves no layout dependency on the removed inline nav', () => {
    expect(stylesSource).not.toMatch(/\.nav-inline/);
  });
});

describe('live channel sources', () => {
  it('uses the real channel list in order instead of an operator picker', () => {
    // 資料庫主鍵不是從 1 起算（實機上是 91/93/94）；與其讓操作員猜編號再勾選，
    // 即時監看直接照 /api/channels 的順序分批開播。選台清單與獨立「開啟」都拿掉。
    expect(indexSource).not.toContain('id="live-channels"');
    for (const [input, list] of [['snap-channel', 'snap-channel-options'], ['ptz-channel', 'ptz-channel-options']]) {
      expect(indexSource, input).toMatch(new RegExp(`id="${input}"[^>]*list="${list}"`));
      expect(indexSource, list).toMatch(new RegExp(`<datalist id="${list}">`));
    }
    expect(appSource).toMatch(/api\('\/api\/channels'\)/);
    expect(appSource).toMatch(/localChannels = \(Array\.isArray\(rows\) \? rows : \[\]\)\.filter\(\(c\) => c\.enabled !== false\)/);
    expect(appSource).toMatch(/Promise\.allSettled\(\[[\s\S]*refreshLocalChannels\(\)[\s\S]*\]\)/);
  });
});

describe('multi-split live grid', () => {
  it('offers 1/4/9/16/25 layouts as clickable buttons', () => {
    // 版面值就是格數；少一顆代表某種電視牆尺寸點不到。
    // 必須是按鈕：select 再選相同的值不會觸發 change，就換不了下一批。
    expect(indexSource).toMatch(/<div id="live-layout"[^>]*class="layout-picker"[^>]*role="group"/);
    expect(indexSource).not.toMatch(/<select id="live-layout"/);
    for (const n of [1, 4, 9, 16, 25]) {
      expect(indexSource, `data-layout ${n}`).toMatch(new RegExp(`data-layout="${n}"`));
    }
  });

  it('renders one <video> cell per layout slot', () => {
    expect(indexSource).toMatch(/id="live-grid"[^>]*class="live-grid"/);
    expect(appSource).toMatch(/grid\.innerHTML = Array\.from\(\{ length: count \},/);
    expect(appSource).toMatch(/const count = liveLayoutCount\(\)/);
  });

  it('opens one WHEP session per channel in the current batch', () => {
    // 每格各自一條會話；批次最多就是版面格數，不逐格等待（ICE 最長 3 秒會拖垮 5x5）。
    expect(appSource).toMatch(/const shown = livePageChannels\(\)/);
    expect(appSource).toMatch(/viewer: createLiveViewer\(\{ apiRaw, t \}\)/);
    expect(appSource).toMatch(/viewer\.open\(channelId, video/);
    expect(appSource).toMatch(/await Promise\.all\(/);
  });

  it('sends channel ids to WHEP, never the channel rows themselves', () => {
    // 分頁直接 slice /api/channels 會拿到整列物件：viewer.open 驗證失敗，
    // 角標補上「頻道須為正整數」後就變成 #[object Object]·頻道須為正整數。
    expect(appSource).toMatch(/return liveChannelIds\(localChannels\.slice\(page \* size, page \* size \+ size\)\)/);
    expect(appSource).toMatch(/Number\(livePageChannels\(\)\[liveCellIndex\(cell\)\] \?\? 0\)/);
  });

  it('advances to the next batch when the same layout is pressed again', () => {
    // 4 分割第一次 1–4，再按一次 5–8；按不同格數則回到第一批。
    // 頁次取模，才不會在最後一批再按時卡死或開出空畫面。
    expect(appSource).toMatch(/if \(next === liveLayout\) livePage \+= 1;/);
    expect(appSource).toMatch(/liveLayout = next;[\s\S]{0,80}livePage = 0;/);
    expect(appSource).toMatch(/const page = livePage % pages;/);
    expect(appSource).toMatch(/localChannels\.slice\(page \* size, page \* size \+ size\)/);
  });

  it('ignores a stale batch that finishes connecting after a newer click', () => {
    // 連續按同一版面時，舊的 ICE 協商可能較晚回來；讓它寫訊息列就會變成
    // 「畫面是 5–8、文字還寫 1–4」，操作員無從判斷現在看的是哪一批。
    expect(appSource).toMatch(/const generation = \+\+liveOpenGeneration;/);
    expect(appSource).toMatch(/await stopLiveView\(\{ external: false \}\)/);
    expect(appSource.match(/if \(generation !== liveOpenGeneration\) return;/g)).toHaveLength(3);
  });

  it('shows which layout and batch is live', () => {
    expect(appSource).toMatch(/btn\.setAttribute\('aria-pressed', String\(active\)\)/);
    expect(appSource).toMatch(/t\('live\.pageMeta', \{ page, pages \}\)/);
    expect(indexSource).toMatch(/id="live-layout"[^>]*data-i18n-attr="aria-label:live\.layout,title:live\.layoutHint"/);
    expect(i18nSource).toContain("'live.layoutHint': '");
  });

  it('asks for the sub stream whenever more than one cell is shown', () => {
    // 現場回報「多格監看有些格等 24 秒以上仍無影格」：主流是 1080p，伺服器端要先解碼再重新
    // 編碼，實測 10 路併發首包 2.6–24.3 秒；次碼流全部 2.2–3.0 秒。單格才用主流。
    // 少了這個選擇不會有任何錯誤訊息——只是有一格一直黑著。
    expect(appSource).toMatch(/const stream = shown\.length > 1 \? 'sub' : 'main'/);
    expect(appSource).toMatch(/viewer\.open\(channelId, video[\s\S]{0,120}\{ stream \}/);
  });

  it('stops every cell session together', () => {
    expect(appSource).toMatch(/cells\.map\(\(\{ viewer \}\) => viewer\.stop\(\)\)/);
  });

  it('releases every cell session when the page goes away', () => {
    // 關分頁不會送出「停止」：沒有任何呼叫端會呼叫 stopLiveView()，而那一條會話也不會逾時
    // （媒體還在流，每個封包都刷新活動時間），於是觀看名額與 ffmpeg 被一個不存在的瀏覽器
    // 一直佔著。pagehide 是唯一還來得及送 DELETE 的時機（見 live.js 的 keepalive）。
    expect(appSource).toMatch(/window\.addEventListener\('pagehide'/);
    expect(appSource).toMatch(/releaseLiveSessionsOnUnload\(\);/);
    expect(appSource).toMatch(/liveCells\b[\s\S]{0,200}viewer\.stop\(\)/);
  });

  it('says there are no cameras instead of opening zero feeds', () => {
    expect(appSource).toMatch(/if \(shown\.length === 0\)[\s\S]*t\('live\.noChannels'\)/);
    expect(i18nSource).toContain("'live.noChannels': '");
    expect(appSource).not.toContain("t('live.pickChannel')");
  });
});

describe('web settings', () => {
  it('adds a settings tab wired to its panel', () => {
    expect(indexSource).toMatch(/id="tabbtn-settings"[^>]*data-tab="settings"[^>]*aria-controls="panel-settings"/);
    expect(indexSource).toMatch(/id="panel-settings"[^>]*data-panel="settings"[^>]*role="tabpanel"[^>]*aria-labelledby="tabbtn-settings"/);
  });

  it('exposes every retention/auth field in the markup', () => {
    for (const id of [
      'set-auth-enabled',
      'set-lock-threshold',
      'set-lock-minutes',
      'set-retention-days',
      'set-watermark-gb',
      'set-alarm-retention',
      'set-audit-retention',
    ]) {
      expect(indexSource, id).toMatch(new RegExp(`id="${id}"`));
    }
    expect(indexSource).toMatch(/data-i18n="setting\.save"/);
    expect(i18nSource).toContain("'setting.auditRetention': '稽核保留'");
  });

  it('loads the config card through the authenticated transport', () => {
    // configCard 是 lib.js 的純函式，負責把回應欄位正規化（大小寫／預設）；必須真的 import。
    expect(appSource).toMatch(/const card = configCard\(await api\('\/api\/config'\)\)/);
    expect(appSource).toMatch(/^\s*configCard,\s*$/m);
  });

  it('saves through the single transport with a JSON body', () => {
    expect(appSource).toMatch(/const body = \{[\s\S]*recordingRetentionDays:[\s\S]*\};/);
    expect(appSource).toMatch(/apiRaw\('\/api\/config', \{/);
    expect(appSource).toMatch(/method: 'PUT'/);
    expect(appSource).toMatch(/body: JSON\.stringify\(body\)/);
    expect(appSource).toMatch(/\$\('settings-form'\)\.addEventListener\('submit', saveSettings\)/);
  });

  it('loads on tab open instead of on the poll', () => {
    // 每 12 秒輪詢覆寫會蓋掉正在編輯的欄位，所以只在開啟設定分頁時載入。
    expect(appSource).toMatch(/if \(name === 'settings'\) void loadSettings\(\)/);
    expect(appSource).not.toMatch(/Promise\.allSettled\(\[[\s\S]*loadSettings\(\)/);
  });

  it('reports load/save outcomes and usage via the catalog', () => {
    expect(appSource).toMatch(/api\('\/api\/config\/usage'\)/);
    expectLocalized(appSource, 'setting.saved', '設定已儲存');
    expectLocalized(appSource, 'setting.saveFailed', '儲存設定失敗');
    expectLocalized(appSource, 'setting.loadFailed', '載入設定失敗');
    expect(appSource).toContain("t('setting.usage',");
    expect(i18nSource).toContain("'setting.usage': '");
  });
});

describe('event query form', () => {
  it('事件頁進場就填入與原本相同的最近二十四小時', () => {
    // 空著日期欄會讓操作員以為一定要知道確切格式；先給一段可用的預設範圍，
    // 搜尋行為才與加欄位之前一致（原本固定是最近 24 小時）。
    expect(appSource).toMatch(/defaultPlayWindow\(\);\s*defaultEventWindow\(\);/);
    expect(appSource).toMatch(/function defaultEventWindow\(\)[\s\S]*?\$\('event-from'\)\.value = toLocalInputValue\(from\)[\s\S]*?\$\('event-to'\)\.value = toLocalInputValue\(now\)/);
  });

  it('事件搜尋使用表單時間範圍而不是另外寫死的區間', () => {
    // 後端 /api/events/search 本來就收 from/to；加欄位後若還用固定的一天，
    // 操作員選了上個月的時段也只會看到今天的結果，且沒有任何錯誤。
    expect(appSource).toMatch(/const range = eventQueryWindow\(\);\s*if \(!range\) return;/);
    expect(appSource).toMatch(/\$\('event-from'\)\?\.value/);
    expect(appSource).toMatch(/\$\('event-to'\)\?\.value/);
    expect(appSource).toMatch(/new URLSearchParams\(\{ q, from: range\.from, to: range\.to, limit: String\(limit\) \}\)/);
    expectLocalized(appSource, 'state.needRange', '請填有效的起訖時間（結束須晚於開始）');
  });

  it('來源類型下拉用位元過濾既有搜尋結果', () => {
    // ForensicSource 是位元旗標（一筆事件可能同時是警報與邊緣 AI）；
    // 用相等比較會把複合來源全部濾掉，看起來像「事件不見了」。
    // 下拉只是畫面上的本機過濾，不許為它另開 API。
    expect(appSource).toMatch(/const FORENSIC_SOURCE_FILTERS = \{ all: 0, alarm: 1, access: 2, pos: 4, edge: 8 \}/);
    expect(appSource).toMatch(/!sourceFilter \|\| \(row\.source & sourceFilter\) !== 0/);
    expect(appSource).toMatch(/\$\('event-type'\)\?\.value/);
    expect(appSource).toMatch(/\/api\/events\/search\?\$\{params\}/);
  });
});

describe('event to playback jump', () => {
  it('只有警報事件有回放按鈕且事件資料都經過esc', () => {
    // 門禁／POS／AI 命中沒有 channel，硬帶一個號碼只會播到別台；
    // data-* 的值來自後端，一定要 esc() 才能進 innerHTML。
    expect(appSource).toMatch(/const FORENSIC_SOURCE_ALARM = 1;/);
    expect(appSource).toMatch(/const canReplay = source === FORENSIC_SOURCE_ALARM/);
    expect(appSource).toMatch(/data-event-id="\$\{esc\(id\)\}" data-event-at="\$\{esc\(at\)\}"/);
    expect(appSource).toMatch(/title="\$\{tx\('event\.toReplay'\)\}"/);
    expect(appSource).toMatch(/>\$\{tx\('event\.toReplay'\)\}<\/button>/);
  });

  it('用既有events端點依id回查頻道且查不到時讓操作員自己確認', () => {
    // 搜尋命中沒有 channel；與其新增後端端點，用事件時刻 ±1 秒的視窗問既有 /api/events，
    // 再依 alarm id 比對。查不到就回 0，不能猜一個號碼讓證據指到別台。
    const body = appSource.slice(
      appSource.indexOf('async function resolveAlarmChannel'),
      appSource.indexOf('/** 設定頁'),
    );
    expect(body).toMatch(/new Date\(center - 1000\)\.toISOString\(\)/);
    expect(body).toMatch(/new Date\(center \+ 1000\)\.toISOString\(\)/);
    expect(body).toMatch(/\/api\/events\?\$\{params\}/);
    expect(body).toMatch(/\.find\(\(item\) => Number\(item\.id\) === alarmId\)/);
    expect(body).toMatch(/Number\(match\?\.channelId \?\? 0\)/);
    expect(body).toMatch(/catch \{\s*return 0;/);
  });

  it('帶入事件前後時段並切到播放器但不自動載入', () => {
    // 自動 submit 等於點一下事件就開一條 ffmpeg；先讓操作員看到時段與頻道再按載入。
    const body = appSource.slice(
      appSource.indexOf('async function jumpEventToReplay'),
      appSource.indexOf('/** 設定頁'),
    );
    expect(appSource).toMatch(/const EVENT_REPLAY_WINDOW_MS = 30_000;/);
    expect(body).toMatch(/\$\('play-from'\)\.value = toLocalInputValue\(new Date\(at\.getTime\(\) - EVENT_REPLAY_WINDOW_MS\)\)/);
    expect(body).toMatch(/\$\('play-to'\)\.value = toLocalInputValue\(new Date\(at\.getTime\(\) \+ EVENT_REPLAY_WINDOW_MS\)\)/);
    expect(body).toMatch(/showTab\('playback'\)/);
    expect(body).toMatch(/showSubTab\('playback', 'player'\)/);
    expect(body).not.toMatch(/requestSubmit|\.submit\(\)/);
    // 事件頻道可能已停用、不在下拉清單裡；先補 option 再選，select 才不會空白。
    expect(body).toMatch(/sel\.add\(new Option\(`#\$\{channelId\}`, String\(channelId\)\)\)/);
    expect(body).toMatch(/channelId > 0\s*\? t\('event\.replayReady'/);
    expect(body).toMatch(/t\('event\.replayChannelUnknown'\)/);
  });

  it('事件列用委派綁定且四個譯文鍵兩語齊備', () => {
    // 列是 innerHTML 重繪出來的：每次重綁每顆按鈕遲早漏一顆，委派才是穩的。
    expect(appSource).toContain("closest?.('.event-replay')");
    expect(appSource).toMatch(/\$\('events'\)\.querySelector\('tbody'\)\.addEventListener\('click'/);
    for (const key of [
      'event.toReplay',
      'event.replayReady',
      'event.replayChannelUnknown',
      'event.replayBadTime',
    ]) {
      expect(appSource, key).toContain(key);
      expect(i18nSource, key).toMatch(new RegExp(`'${key.replace('.', '\\.')}':`));
    }
    expect(indexSource, '動作欄').toMatch(/data-i18n="col\.action"/);
  });
});

describe('playback toolbar', () => {
  it('三十秒跳躍只改本機游標且上界用seekable而不是無限的duration', () => {
    // MSE 的 duration 常是 Infinity；拿它當上界會讓 +30 秒跳到不存在的位置、畫面停空白。
    expect(appSource).toMatch(/if \(!Number\.isFinite\(upper\) && video\.seekable\?\.length > 0\)/);
    expect(appSource).toMatch(/upper = video\.seekable\.end\(video\.seekable\.length - 1\)/);
    expect(appSource).toMatch(/seekPlayback\(-30\)/);
    expect(appSource).toMatch(/seekPlayback\(30\)/);
  });

  it('上一張與下一張共用步長且在暫停之後才移動游標', () => {
    // 把 stepPlaybackFrame 直接當 click 回呼傳進去會收到 Event 物件，delta 變成非數值，
    // 兩顆按鈕都看不出動作；每一邊都要自己帶正負步長。
    expect(appSource).toMatch(/function stepPlaybackFrame\(delta = 1 \/ 30\)[\s\S]*?video\.pause\(\)[\s\S]*?playbackSeekTarget\(delta\)/);
    expect(appSource).toMatch(/\$\('play-prev-frame'\)\?\.addEventListener\('click', \(\) => stepPlaybackFrame\(-1 \/ 30\)\)/);
    expect(appSource).toMatch(/\$\('play-next-frame'\)\?\.addEventListener\('click', \(\) => stepPlaybackFrame\(1 \/ 30\)\)/);
    expect(appSource).not.toMatch(/addEventListener\('click', stepPlaybackFrame\)/);
  });

  it('播放速度改的是本機playbackRate且換段後重新套用', () => {
    // 換上新的 MediaSource 時瀏覽器會重置 playbackRate；只監聽 change 而不在 ready 後重套，
    // 操作員看到的就是「選了 2×，重新載入回放又變回 1×」。
    expect(appSource).toMatch(/function applyPlaybackSpeed\(\)[\s\S]*?video\.playbackRate = Number\.isFinite\(rate\) && rate > 0 \? rate : 1/);
    expect(appSource).toMatch(/\$\('play-speed'\)\?\.addEventListener\('change', applyPlaybackSpeed\)/);
    const playRemoteBody = appSource.slice(
      appSource.indexOf('async function playRemote'),
      appSource.indexOf('function defaultPlayWindow'),
    );
    expect(playRemoteBody).toMatch(/applyPlaybackSpeed\(\);/);
  });

  it('循環靠timeupdate在媒體尾端回帶而不是靠不會發生的ended', () => {
    // 這條 MSE 管線刻意不呼叫 endOfStream（往回拖要再 append），所以 ended 永遠不來，
    // 依賴 ended 的循環等於完全沒有循環。回帶後還要有 armed 開關擋掉舊的 timeupdate。
    const body = appSource.slice(appSource.indexOf('const PLAY_LOOP_EPSILON_SECONDS'));
    expect(body).toMatch(/function maybeLoopPlayback\(\)[\s\S]*?playLoopArmed = false;[\s\S]*?video\.currentTime = 0;[\s\S]*?video\.play\(\)/);
    expect(body).toMatch(/upper - PLAY_LOOP_EPSILON_SECONDS/);
    expect(appSource).toMatch(/\$\('play-video'\)\.addEventListener\('timeupdate', maybeLoopPlayback\)/);
    expect(appSource).toMatch(/\$\('play-loop'\)\?\.addEventListener\('change'/);
    const playRemoteBody = appSource.slice(
      appSource.indexOf('async function playRemote'),
      appSource.indexOf('function defaultPlayWindow'),
    );
    expect(playRemoteBody).toMatch(/playLoopArmed = true;/);
    expect(appSource).not.toMatch(/addEventListener\('ended', maybeLoopPlayback\)/);
  });

  it('全螢幕交給video元件而不是自己改造覆蓋層', () => {
    expect(appSource).toMatch(/video\.requestFullscreen\?\.\(\)/);
    expect(appSource).toMatch(/document\.exitFullscreen\?\.\(\)/);
  });
});
