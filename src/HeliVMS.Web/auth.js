// 共用的憑證與 transport。獨立登入頁（login.html）與主介面（index.html）都載入這一支，
// 因此全站仍然只有一個 fetch 出口：萬一任一方自己寫一份，bearer header 就會漏送，
// 而症狀是「登入成功但每一支 API 都 401」，兩邊日誌都乾淨。
import { t } from './i18n.js';
import { bearerHeader } from './lib.js';

const KEY_STORAGE = 'helivms.apiKey';
const SESSION_STORAGE = 'helivms.session';

/** 目前生效的憑證。主介面已經沒有金鑰欄，所以只讀儲存體。 */
export function key() {
  return (localStorage.getItem(KEY_STORAGE) || '').trim();
}

export function setApiKey(token) {
  localStorage.setItem(KEY_STORAGE, String(token ?? '').trim());
}

export function clearApiKey() {
  localStorage.removeItem(KEY_STORAGE);
}

export function readSession() {
  try {
    return JSON.parse(sessionStorage.getItem(SESSION_STORAGE) || 'null');
  } catch {
    return null;
  }
}

export function storeSession(session) {
  if (session) {
    sessionStorage.setItem(SESSION_STORAGE, JSON.stringify(session));
  } else {
    sessionStorage.removeItem(SESSION_STORAGE);
  }
}

/** 連線旗標：任何請求成功即可正常；網路層失敗時在頂列顯示「連線中斷」。登入頁沒有 #conn，安全跳過。 */
function setConn(ok) {
  const el = document.getElementById('conn');
  if (!el) return;
  el.textContent = ok ? '' : t('state.disconnected');
  el.classList.toggle('off', !ok);
}

/** 單一已驗證 transport：兩頁的每一支 API 呼叫都必須經過這裡，bearer 才不會漏。 */
export async function apiRaw(path, init = {}) {
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

/**
 * 用真實請求判斷是否可直接進入主介面。不能只看 localStorage 有沒有 session/key：
 * 伺服器重開後 AccountSessionStore 是空的，舊 token 還留在瀏覽器裡，只靠本機值放行
 * 就會回到「主介面看得到、每一支 API 都 401」的舊症狀。/api/config 在一般部署需要
 * 有效 Bearer，HELIVMS_API_KEY_DISABLED=1 的部署不需金鑰就會回 200。
 */
export async function hasAccess() {
  try {
    const res = await apiRaw('/api/config');
    return res.ok;
  } catch {
    return false;
  }
}
