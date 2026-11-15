import { execFileSync } from 'node:child_process';
import { copyFileSync, mkdtempSync, readdirSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const root = fileURLToPath(new URL('.', import.meta.url));
const scripts = readdirSync(root).filter((f) => f.endsWith('.js') && !f.endsWith('.test.js'));

/**
 * 出貨的每一支模組都要真的能被 JavaScript 引擎解析。
 *
 * 這條測試存在的理由是一個實測到的缺口：這個套件的其他測試**都只把 app.js 當文字讀**
 * （原始碼契約、跳脫掃描、i18n 檢查），沒有任何一條會解析它。所以 `a ?? b || c` 這種
 * 語法錯誤（`??` 與 `||` 未加括號）可以通過全部測試、通過 CI，然後在瀏覽器裡讓整個
 * 模組載入失敗——畫面全白、Console 只有一行 SyntaxError，看起來正是操作員回報的
 * 「WEB 介面看不到畫面」。伺服器、API、日誌都正常，因為頁面根本沒有執行起來。
 *
 * `node --check` 對 ESM 只在 .mjs 上生效，所以複製成 .mjs 再檢查；複製到暫存目錄
 * 是為了不改動原始檔（保持 CRLF 與檔名）。
 */
describe('shipped scripts parse', () => {
  it('covers every shipped module, not just app.js', () => {
    expect(scripts).toContain('app.js');
    expect(scripts).toContain('live.js');
    expect(scripts.length).toBeGreaterThanOrEqual(5);
  });

  it.each(scripts)('%s is syntactically valid', (name) => {
    const dir = mkdtempSync(join(tmpdir(), 'helivms-parse-'));
    const copy = join(dir, name.replace(/\.js$/, '.mjs'));
    try {
      copyFileSync(join(root, name), copy);
      execFileSync(process.execPath, ['--check', copy], { stdio: 'pipe' });
    } catch (err) {
      throw new Error(`${name} 無法解析：${err.stderr?.toString() ?? err.message}`);
    } finally {
      rmSync(dir, { recursive: true, force: true });
    }
  });
});
