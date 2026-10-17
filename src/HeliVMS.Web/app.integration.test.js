import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
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

describe('api-key flow', () => {
  it('refreshes immediately when the API key is committed with Enter', () => {
    expect(appSource).toMatch(/apikey'\)\.addEventListener\('keydown'[\s\S]*Enter[\s\S]*refreshAll\(\)/);
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

  it('flips the banner inside the single transport around fetch', () => {
    expect(appSource).toMatch(/const response = await fetch\(path, \{ \.\.\.init, headers \}\);\s*setConn\(true\);/);
    expect(appSource).toMatch(/catch \(err\) \{\s*setConn\(false\);\s*throw err;\s*\}/);
  });

  it('renders a disconnect label and hides empty state', () => {
    expect(appSource).toMatch(/el\.textContent = ok \? '' : t\('state\.disconnected'\);/);
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
    expect(appSource).toMatch(/\$\('live-form'\)\.addEventListener\('submit', openLiveView\)/);
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
    expect(appSource).toMatch(/body: JSON\.stringify\(\{ profileToken: ptzProfileToken, pan, tilt, zoom \}\)/);
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

  it('moves while held and stops on release', () => {
    // ONVIF ContinuousMove 是速度命令；點一下就走一步的相機放開後會持續移動。
    expect(appSource).toMatch(/btn\.addEventListener\('pointerdown', issue\)/);
    expect(appSource).toMatch(/document\.addEventListener\('pointerup', \(\) => void ptzStop\(\)\)/);
    expect(appSource).toMatch(/\$\('ptz-stop'\)\.addEventListener\('click', \(\) => void ptzStop\(\)\)/);
  });

  it('refuses to move without a connected profile', () => {
    expect(appSource).toMatch(/if \(!ptzProfileToken\) \{[\s\S]*t\('ptz\.notSupported'\)/);
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
    expect(appSource).toMatch(/if \(playlist\.initUri\) await push\(playlist\.initUri\)/);
    expect(appSource).toMatch(/for \(const segment of playlist\.segments\) await push\(segment\.uri\)/);
    expect(appSource).toMatch(/const response = await apiRaw\(uri\)/);
    expect(appSource).toMatch(/media\.endOfStream\(\)/);
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

  it('feeds child-node channels into the playback suggestions', () => {
    expect(indexSource).toMatch(/id="play-channel"[^>]*list="play-channel-options"/);
    expect(indexSource).toMatch(/<datalist id="play-channel-options">/);
    expect(appSource).toMatch(/centralChannelRows\(await api\('\/api\/central\/channels'\)/);
    // 函式必須真的 import 進來：呼叫包在 catch 裡時，ReferenceError 會被吞掉。
    expect(appSource).toMatch(/^\s*centralChannelRows,\s*$/m);
    expect(appSource).toMatch(/\$\('play-node'\)\?\.addEventListener\('change', fillPlayChannels\)/);
  });

  it('reserves no layout dependency on the removed inline nav', () => {
    expect(stylesSource).not.toMatch(/\.nav-inline/);
  });
});

describe('channel picker', () => {
  it('offers the real channel ids instead of making operators guess', () => {
    // 資料庫主鍵不是從 1 起算（實機上是 91/93/94）；沒有這份清單只能靠猜編號，
    // 猜錯時畫面全黑、訊息只寫「找不到這一路攝影機」。
    expect(indexSource).toMatch(/<select id="live-channels"[^>]*multiple/);
    for (const [input, list] of [['snap-channel', 'snap-channel-options'], ['ptz-channel', 'ptz-channel-options']]) {
      expect(indexSource, input).toMatch(new RegExp(`id="${input}"[^>]*list="${list}"`));
      expect(indexSource, list).toMatch(new RegExp(`<datalist id="${list}">`));
    }
    expect(appSource).toMatch(/api\('\/api\/channels'\)/);
    expect(appSource).toMatch(/Promise\.allSettled\(\[[\s\S]*refreshLocalChannels\(\)[\s\S]*\]\)/);
  });
});

describe('multi-split live grid', () => {
  it('offers 1/4/9/16/25 layouts in the markup', () => {
    // 版面值就是格數；少一個選項代表某種電視牆尺寸點不到。
    expect(indexSource).toMatch(/<select id="live-layout"/);
    for (const n of [1, 4, 9, 16, 25]) {
      expect(indexSource, `option ${n}`).toMatch(new RegExp(`<option value="${n}"`));
    }
  });

  it('renders one <video> cell per layout slot', () => {
    expect(indexSource).toMatch(/id="live-grid"[^>]*class="live-grid"/);
    expect(appSource).toMatch(/grid\.innerHTML = Array\.from\(\{ length: count \},/);
    expect(appSource).toMatch(/const count = liveLayoutCount\(\)/);
  });

  it('opens one WHEP session per selected channel, capped at the layout', () => {
    // 每格各自一條會話；選超過格數只取前 N 路，不逐格等待（ICE 最長 3 秒會拖垮 5x5）。
    expect(appSource).toMatch(/channels\.slice\(0, liveLayoutCount\(\)\)/);
    expect(appSource).toMatch(/viewer: createLiveViewer\(\{ apiRaw, t \}\)/);
    expect(appSource).toMatch(/viewer\.open\(channelId, video/);
    expect(appSource).toMatch(/await Promise\.all\(/);
  });

  it('stops every cell session together', () => {
    expect(appSource).toMatch(/cells\.map\(\(\{ viewer \}\) => viewer\.stop\(\)\)/);
  });

  it('warns when nothing is selected instead of opening zero feeds', () => {
    expect(appSource).toMatch(/if \(channels\.length === 0\)[\s\S]*t\('live\.pickChannel'\)/);
    expect(i18nSource).toContain("'live.pickChannel': '");
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
