// 獨立登入頁。這裡只處理「拿到憑證」這件事：成功後帶著 token 跳轉主介面，
// 主介面用同一份 auth.js 讀取憑證，不再需要把自己的一部份藏進登入元件裡。
import { applyDom, DEFAULT_LOCALE, locale, setLocale, t } from './i18n.js';
import { parseLogin } from './lib.js';
import { apiRaw, clearApiKey, hasAccess, key, setApiKey, storeSession } from './auth.js';

const $ = (id) => document.getElementById(id);
const LOCALE_KEY = 'helivms.locale';
const MAIN_PAGE = 'index.html';

function storedLocale() {
  try {
    return window.localStorage.getItem(LOCALE_KEY) ?? DEFAULT_LOCALE;
  } catch {
    return DEFAULT_LOCALE;
  }
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
  if (!state.ok) return;
  storeSession({ role: state.role, name: state.displayName });
  // 登入成功即拿到工作階段 token；寫進共用憑證槽，讓主介面的每一支 API 都帶它。
  if (state.token) setApiKey(state.token);
  window.location.replace(MAIN_PAGE);
}

/** 以 API 金鑰進入：先試再寫入，失敗時復原舊值，免得把有效憑證洗掉。 */
async function enterWithKey() {
  const value = $('apikey').value.trim();
  if (!value) {
    $('login-msg').textContent = t('login.needKey');
    return;
  }
  const previous = key();
  setApiKey(value);
  if (await hasAccess()) {
    window.location.replace(MAIN_PAGE);
    return;
  }
  if (previous) setApiKey(previous);
  else clearApiKey();
  $('login-msg').textContent = t('state.loginFailed');
}

/** 語系：與主介面共用 localStorage 的選擇，但登入頁沒有那些需要重繪的動態清單。 */
function applyLocale(next, persist = true) {
  const applied = setLocale(next);
  if (persist) {
    try {
      window.localStorage.setItem(LOCALE_KEY, applied);
    } catch {
      /* 儲存不可用時忽略：語系仍然生效，只是不會被記住 */
    }
  }
  applyDom(document);
  $('login-locale').value = applied;
  return applied;
}

async function boot() {
  setLocale(storedLocale());
  applyDom(document);
  $('login-locale').value = locale();

  $('login').addEventListener('submit', submitLogin);
  $('login-key').addEventListener('click', () => { void enterWithKey(); });
  $('apikey').addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      void enterWithKey();
    }
  });
  $('login-locale').addEventListener('change', (e) => {
    void applyLocale(e.target.value);
  });
  // 登入頁一開始就看得見，靜態 autofocus 會生效；仍用 rAF 避免腳本執行時焦點被瀏覽器搶走。
  requestAnimationFrame(() => $('user')?.focus());

  // 已經有可用憑證（或部署根本免金鑰）時不必停在登入頁；
  // 主介面在不同的 Authenticate 狀態下仍會自行驗證一次，兩邊不會互相迴圈。
  if (await hasAccess()) {
    window.location.replace(MAIN_PAGE);
  }
}

boot();
