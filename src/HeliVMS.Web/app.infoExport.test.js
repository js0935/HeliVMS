import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
const i18nSource = readFileSync(join(root, 'i18n.js'), 'utf8');

/**
 * 裝置資訊與匯出分頁（對齊 QCV8001 web 的 Info／Export 主項）。
 *
 * 參考的 device web 有四大主項：即時（Live）／回放（Player）／匯出（Export）／
 * 資訊（Info）。SPA 原本缺後兩個：操作員要查「這台機器是什麼版本、磁碟還能錄多久」
 * 得開桌面程式；要帶走一段錄影也沒有遠端入口。這裡釘住兩個分頁的接線與安全規則。
 */
describe('device info panel', () => {
  it('is wired as a sub-tab of 設定 with its own panel', () => {
    // 裝置資訊不再是頂層頁籤：四個主頁（監看／回放／事件／設定）之外的東西一律收進
    // 所屬主頁，否則九個平鋪頁籤就是操作員回報的「功能雜亂」。
    expect(indexSource).toMatch(/data-subtab="info"[^>]*aria-controls="subpanel-info"/);
    expect(indexSource).toMatch(/id="subpanel-info"[^>]*data-subpanel="info"/);
    expect(appSource).toMatch(/if \(group === 'settings' && name === 'info'\) void refreshDeviceInfo\(\);/);
  });

  it('reads the system, license, usage, capacity and disk-health endpoints', () => {
    const body = appSource.match(/async function refreshDeviceInfo\(\)[\s\S]*?async function refreshExports/)?.[0] ?? '';
    expect(body).not.toBe('');
    for (const endpoint of ['/api/health', '/api/license', '/api/config/usage', '/api/recording/capacity', '/api/system/disk-health']) {
      expect(body, endpoint).toContain(`'${endpoint}'`);
    }
  });

  it('renders every read failure as a dash instead of an empty panel', () => {
    const body = appSource.match(/async function refreshDeviceInfo\(\)[\s\S]*?async function refreshExports/)?.[0] ?? '';
    // 五個來源各自 catch(() => null)；模板裡以「—」佔位。
    expect(body).toMatch(/catch\(\(\) => null\)/);
    expect(body).toContain("const dash = '—'");
  });

  it('localizes server-side state words and keeps unknown values verbatim', () => {
    expect(appSource).toMatch(/function localizedState\(key, state\)/);
    expect(appSource).toMatch(/return text === `\$\{key\}\.\$\{value\}` \? value : text;/);
    for (const key of ['info.capacityState.known', 'info.capacityState.at_reserve', 'info.diskState.healthy', 'info.diskState.failing']) {
      expect(i18nSource, key).toContain(`'${key}':`);
    }
  });

  it('shows the remaining recording time only when the state is known', () => {
    // 「沒有量測（unknown）」與「已低於保留空間（at_reserve）」的處置完全相反，
    // 把它們混成 0 會讓「錄影根本沒在跑」看起來像「磁碟滿了」。
    expect(appSource).toMatch(/function formatRemaining\(seconds, state\)/);
    expect(appSource).toMatch(/if \(state !== 'known'\) return '—';/);
  });
});

describe('export panel', () => {
  it('is wired as a sub-tab of 回放 with its own panel and job table', () => {
    // 匯出原本是頂層頁籤，但它與回放是同一個工作流程（找時段 → 帶走），
    // 所以收成回放頁的第二個次分頁。
    expect(indexSource).toMatch(/data-subtab="export"[^>]*aria-controls="subpanel-export"/);
    expect(indexSource).toMatch(/id="subpanel-export"[^>]*data-subpanel="export"/);
    expect(indexSource).toMatch(/id="export-jobs"/);
    expect(appSource).toMatch(/if \(group === 'playback' && name === 'export'\) \{[\s\S]*refreshExportSources\(\);[\s\S]*refreshExports\(\);/);
  });

  it('creates jobs through the existing POST /api/exports endpoint', () => {
    expect(appSource).toMatch(/apiRaw\('\/api\/exports', \{\s*method: 'POST'/);
    expect(appSource).toMatch(/channelId,\s*stream: 'main',\s*fromUtc: new Date\(from\)\.toISOString\(\),\s*toUtc: new Date\(to\)\.toISOString\(\),/);
  });

  it('polls the job list with the shared 12s refresh', () => {
    const body = appSource.match(/async function refreshAll\(\) \{[\s\S]*?\n\}/)?.[0] ?? '';
    expect(body).toContain('refreshExports()');
  });

  it('maps server status words to localized badges', () => {
    for (const key of ['export.status.queued', 'export.status.running', 'export.status.finished', 'export.status.failed']) {
      expect(i18nSource, key).toContain(`'${key}':`);
    }
    expect(appSource).toMatch(/function exportStatusClass\(status\)/);
  });

  it('prefills the time range with local datetime-local values, not ISO UTC', () => {
    // datetime-local 欄位吃本地時間字串；直接塞 toISOString() 會差一個時區。
    expect(appSource).toMatch(/function toLocalInputValue\(date\)/);
    expect(appSource).toMatch(/from\.value = toLocalInputValue\(start\)/);
  });
});
