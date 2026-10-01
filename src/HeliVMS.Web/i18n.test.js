import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

import { DEFAULT_LOCALE, LOCALES, applyDom, catalogs, locale, setLocale, t } from './i18n.js';
import { gapLabel, patrolLabel, scheduleLabel, tx } from './lib.js';

const root = dirname(fileURLToPath(import.meta.url));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const libSource = readFileSync(join(root, 'lib.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');

/** 剝掉註解後的程式碼，避免文件說明被誤判成硬編字串。 */
function code(source) {
  return source.replace(/\/\*[\s\S]*?\*\//g, '').replace(/(^|[^:])\/\/[^\n]*/g, '$1');
}

/** 取出字串／樣板字面值。 */
function stringLiterals(source) {
  const out = [];
  const re = /'([^'\\\n]*)'|"([^"\\\n]*)"|`([^`\\]*)`/g;
  let m;
  while ((m = re.exec(code(source))) !== null) out.push(m[1] ?? m[2] ?? m[3] ?? '');
  return out;
}

/**
 * 介面文字中不該出現的 CJK：漢字與全形標點。
 * 只掃漢字是不夠的——「、」與「（）」沒有任何漢字，
 * 卻會讓英文畫面出現全形逗號與全形括號。
 */
const CJK_IN_UI = /[\u3000-\u303f\u4e00-\u9fff\uff00-\uffef]/;

describe('catalog completeness', () => {
  it('supports exactly the declared locales', () => {
    expect(Object.keys(catalogs).sort()).toEqual([...LOCALES].sort());
  });

  it('has the same key set in every locale', () => {
    // 少一個 key 就代表該語系會在畫面上顯示 ‼key.xxx，
    // 這比英文少一兩個詞嚴重得多，所以必須逐個 key 比對而不是只比數量。
    const [first, ...rest] = LOCALES.map((l) => Object.keys(catalogs[l]).sort());
    for (const keys of rest) {
      expect(keys).toEqual(first);
    }
  });

  it('does not reuse a key with conflicting meanings across locales', () => {
    const zh = catalogs['zh-TW'];
    const en = catalogs.en;
    for (const key of Object.keys(zh)) {
      expect(typeof zh[key]).toBe('string');
      expect(typeof en[key]).toBe('string');
      expect(en[key].length, `en/${key} 不應是空字串`).toBeGreaterThan(0);
    }
  });

  it('keeps the placeholder names identical across locales', () => {
    const placeholders = (s) => [...s.matchAll(/\{(\w+)\}/g)].map((m) => m[1]).sort();
    for (const key of Object.keys(catalogs['zh-TW'])) {
      expect(placeholders(catalogs.en[key]), `en/${key} 的佔位符與 zh-TW 不一致`).toEqual(
        placeholders(catalogs['zh-TW'][key]),
      );
    }
  });
});

describe('t()', () => {
  it('returns the requested locale', () => {
    expect(setLocale('en')).toBe('en');
    expect(t('common.enabled')).toBe('Enabled');
    expect(setLocale('zh-TW')).toBe('zh-TW');
    expect(t('common.enabled')).toBe('啟用');
  });

  it('falls back to the default locale for an unsupported language tag', () => {
    // 拼錯的語系若靜默生效，只會讓畫面變成半中半英，所以退回預設。
    expect(setLocale('fr-FR')).toBe(DEFAULT_LOCALE);
    expect(locale()).toBe(DEFAULT_LOCALE);
  });

  it('interpolates parameters', () => {
    setLocale('zh-TW');
    expect(t('search.found', { total: 7 })).toBe('找到 7 筆');
    setLocale('en');
    expect(t('search.found', { total: 7 })).toBe('Found 7');
  });

  it('leaves unknown placeholders untouched instead of printing undefined', () => {
    setLocale('zh-TW');
    expect(t('search.found', { nope: 1 })).toBe('找到 {total} 筆');
  });

  it('flags a missing key rather than rendering an empty label', () => {
    expect(t('definitely.not.a.key')).toContain('definitely.not.a.key');
  });
});

describe('tx() escapes interpolated parameters', () => {
  it('escapes markup coming from server data', () => {
    setLocale('zh-TW');
    // t() 會原樣內插參數，所以未跳脫的譯文不能直接進 innerHTML；
    // tx() 是進入 HTML 的唯一合法途徑。
    expect(t('state.missingFeatures', { features: '<img src=x>' })).toContain('<img');
    expect(tx('state.missingFeatures', { features: '<img src=x>' })).not.toContain('<img');
    expect(tx('state.missingFeatures', { features: '<img src=x>' })).toContain('&lt;img');
  });

  it('escapes ampersands and quotes too', () => {
    expect(tx('state.unauthorized', { reason: `a&b"c'd` })).toBe(
      '未授權：a&amp;b&quot;c&#39;d',
    );
  });
});

describe('runtime consumers follow the active locale', () => {
  it('translates gap labels', () => {
    setLocale('zh-TW');
    expect(gapLabel(1)).toBe('無錄影');
    expect(gapLabel(0)).toBe('全日');
    expect(gapLabel(0.25)).toBe('缺 25%');

    setLocale('en');
    expect(gapLabel(1)).toBe('No recording');
    expect(gapLabel(0)).toBe('All day');
    expect(gapLabel(0.25)).toBe('25% missing');
  });

  it('translates weekday names in schedules', () => {
    const everyDay = { daysMask: 0b1111111, startMinute: 0, endMinute: 1439 };
    setLocale('zh-TW');
    expect(scheduleLabel(everyDay, { short: true })).toContain('日-六');
    setLocale('en');
    expect(scheduleLabel(everyDay, { short: true })).toContain('Sun-Sat');
  });

  it('translates patrol summaries', () => {
    setLocale('zh-TW');
    expect(patrolLabel({ steps: [1, 2, 3] })).toContain('3 步');
    setLocale('en');
    expect(patrolLabel({ steps: [1, 2, 3] })).toContain('3 steps');
  });
});

/**
 * 以「文字／標籤交替」方式切出所有文字節點。
 *
 * 不要用單一重疊正則（例如 `>([^<>]*CJK[^<>]*)<`）去找：它的 lastIndex 會吃掉
 * 緊接著的下一個 `<`，於是巢狀文字被父層的字吃掉而從未被檢查——
 * `<h2 data-i18n="k">標題 <span>HLS／已錄影</span></h2>` 的 span 文字就這樣漏掉。
 */
function textNodes(html) {
  const parts = html.split(/(<[^>]*>)/);
  const out = [];
  for (let i = 2; i < parts.length; i += 2) {
    if (parts[i - 1].startsWith('</')) continue;
    out.push({ text: parts[i], owner: parts[i - 1], at: parts.slice(0, i).join('').length });
  }
  return out;
}

const CJK_TEXT = /[　-〿一-鿿＀-￯]/;

describe('static markup is wired to the catalog', () => {
  it('marks translatable text with data-i18n', () => {
    const marked = (indexSource.match(/data-i18n="/g) ?? []).length;
    expect(marked).toBeGreaterThan(100);
  });

  it('uses only keys that exist', () => {
    const keys = new Set(Object.keys(catalogs['zh-TW']));
    for (const m of indexSource.matchAll(/data-i18n="([^"]+)"/g)) {
      expect(keys.has(m[1]), `index.html 參照了不存在的 key：${m[1]}`).toBe(true);
    }
    for (const m of indexSource.matchAll(/data-i18n-attr="([^"]+)"/g)) {
      for (const pair of m[1].split(',')) {
        const [, key] = pair.split(':').map((s) => s.trim());
        expect(keys.has(key), `index.html 屬性參照了不存在的 key：${key}`).toBe(true);
      }
    }
  });

  it('does not stamp data-i18n on elements the app owns at runtime', () => {
    // 否則切換語言會把「admin · 王小明」蓋回「未登入」——
    // 這是資料與翻譯爭用同一個節點的典型錯誤。
    //
    // 例外：下載連結在送出失敗時會被 app.js 覆寫成錯誤訊息，但那是暫時狀態；
    // 切換語言時回到「下載 CSV」才是預期畫面，所以刻意保留 data-i18n。
    const ownedButIntentionallyLocalized = new Set(['audit-csv']);

    for (const m of indexSource.matchAll(/<(\w+)[^>]*\bid="([\w-]+)"[^>]*data-i18n="[^"]*"[^>]*>/g)) {
      const id = m[2];
      if (ownedButIntentionallyLocalized.has(id)) continue;
      const assigned = new RegExp(
        `\\$\\('${id}'\\)\\.(textContent|innerHTML|value)\\s*=|const \\w+ = \\$\\('${id}'\\)`,
      );
      expect(assigned.test(appSource), `#${id} 由 app.js 改寫，不應掛 data-i18n`).toBe(false);
    }
  });

  it('never puts data-i18n on an element that has child elements', () => {
    // applyDom 用 textContent 覆寫整個節點，所以帶 data-i18n 的元素一旦還有
    // 子元素，切換語言就會把那些子元素刪掉——<label>文字 <input></label> 會讓
    // 輸入框整個消失，<h2>標題 <span id="...-count"></span></h2> 會讓計數徽章消失。
    // 混合內容必須把 data-i18n 放在只包住文字的內層 span 上。
    const parts = indexSource.split(/(<[^>]*>)/);
    for (let i = 1; i < parts.length; i += 2) {
      const open = parts[i];
      if (!/data-i18n="/.test(open)) continue;
      const tag = /^<(\w+)/.exec(open)?.[1];
      if (!tag) continue;
      const closeAt = indexSource.indexOf(`</${tag}>`, parts.slice(0, i + 1).join('').length);
      if (closeAt < 0) continue;
      const inner = indexSource.slice(parts.slice(0, i + 1).join('').length, closeAt);
      const child = /<\s*(\w+)/.exec(inner);
      const line = indexSource.slice(0, parts.slice(0, i).join('').length).split('\n').length;
      expect(child, `L${line} 的 <${tag}> 同時帶 data-i18n 與子元素 <${child?.[1] ?? '?'}>，` +
        `applyDom 會刪掉它；請把 data-i18n 下移到內層 span`).toBe(null);
    }
  });

  it('leaves no Chinese text unmapped in the visible markup', () => {
    // 未登入由 app.js 依登入狀態改寫；語言名稱刻意維持母語（endonym），
    // 使用者要靠它認出該切到哪一種語言，翻成英文反而認不出來。
    const intentionallyLeft = new Set(['未登入', '繁體中文']);
    const leftovers = [];
    for (const n of textNodes(indexSource)) {
      const text = n.text.trim();
      if (!text || !CJK_TEXT.test(text) || intentionallyLeft.has(text)) continue;
      if (/data-i18n="/.test(n.owner)) continue;
      const line = indexSource.slice(0, n.at).split('\n').length;
      leftovers.push(`L${line} ${JSON.stringify(text)}`);
    }
    expect(leftovers, `index.html 仍有未翻譯的文字：${leftovers.join(' / ')}`).toEqual([]);
  });

  it('leaves no Chinese text unmapped in the attributes', () => {
    const leftovers = [];
    for (const m of indexSource.matchAll(/<[^>]*>/g)) {
      if (/data-i18n-attr/.test(m[0])) continue;
      for (const a of m[0].matchAll(/\b(placeholder|aria-label|title)="([^"]+)"/g)) {
        if (CJK_TEXT.test(a[2])) leftovers.push(a[0]);
      }
    }
    expect(leftovers, `index.html 仍有未翻譯的屬性：${leftovers.join(' / ')}`).toEqual([]);
  });
});

describe('ratchet: no hardcoded UI strings in JavaScript', () => {
  it('keeps Chinese out of app.js and lib.js string literals', () => {
    // 這是棘輪：介面文字一旦又寫回程式碼，切換語言就會出現混雜畫面。
    const offenders = [
      ...stringLiterals(appSource).map((s) => `app.js: ${s}`),
      ...stringLiterals(libSource).map((s) => `lib.js: ${s}`),
    ].filter((s) => CJK_IN_UI.test(s));
    expect(offenders, `介面字串被硬編回去：${offenders.join(' / ')}`).toEqual([]);
  });

  it('reaches the catalog through t() or tx(), never raw concatenation', () => {
    expect(appSource).toMatch(/from '\.\/i18n\.js'/);
    expect(libSource).toMatch(/from '\.\/i18n\.js'/);
  });

  it('offers a language picker wired to the catalog', () => {
    expect(indexSource).toMatch(/<select id="locale"/);
    expect(appSource).toMatch(/addEventListener\('change',[\s\S]{0,80}applyLocale/);
    // 切換後必須重繪執行時產生的內容，而不只是靜態標記。
    expect(appSource).toMatch(/applyDom\(document\)/);
    expect(appSource).toMatch(/async function applyLocale[\s\S]*await refreshAll\(\)/);
  });
});

describe('applyDom', () => {
  it('rewrites text and attributes and sets the document language', () => {
    setLocale('en');
    const textNodes = [{ dataset: { i18n: 'nav.channels' }, textContent: '' }];
    const attrNodes = [
      {
        dataset: { i18nAttr: 'placeholder:ph.channel' },
        attrs: {},
        setAttribute(k, v) {
          this.attrs[k] = v;
        },
      },
    ];
    const root = {
      documentElement: { lang: '' },
      querySelectorAll: (selector) =>
        selector === '[data-i18n]' ? textNodes : selector === '[data-i18n-attr]' ? attrNodes : [],
    };

    applyDom(root);

    expect(textNodes[0].textContent).toBe('Channels');
    expect(attrNodes[0].attrs.placeholder).toBe('Channel');
    expect(root.documentElement.lang).toBe('en');
  });
});