import { describe, expect, it } from 'vitest';
import { readFileSync } from 'node:fs';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = join(dirname(fileURLToPath(import.meta.url)));
const appSource = readFileSync(join(root, 'app.js'), 'utf8');
const indexSource = readFileSync(join(root, 'index.html'), 'utf8');
const i18nSource = readFileSync(join(root, 'i18n.js'), 'utf8');

describe('camera IO contacts (ONVIF DeviceIO)', () => {
  it('adds an IO tab wired to its panel', () => {
    expect(indexSource).toMatch(/id="tabbtn-io"[^>]*data-tab="io"[^>]*aria-controls="panel-io"/);
    expect(indexSource).toMatch(/id="panel-io"[^>]*data-panel="io"[^>]*role="tabpanel"[^>]*aria-labelledby="tabbtn-io"/);
  });

  it('offers a channel picker seeded from the local channel list', () => {
    expect(indexSource).toMatch(/id="io-channel"[^>]*list="io-channel-options"/);
    expect(indexSource).toMatch(/<datalist id="io-channel-options">/);
    expect(appSource).toMatch(/'io-channel-options'/);
    expect(appSource).toMatch(/\['snap-channel', 'ptz-channel', 'io-channel'\]/);
  });

  it('loads inputs and outputs through the authenticated transport', () => {
    expect(appSource).toMatch(/apiRaw\(`\/api\/io\/\$\{channelId\}\/inputs`\)/);
    expect(appSource).toMatch(/apiRaw\(`\/api\/io\/\$\{channelId\}\/outputs`\)/);
  });

  it('switches a relay with the token and target state', () => {
    expect(appSource).toMatch(/\/api\/io\/\$\{channelId\}\/outputs\/\$\{encodeURIComponent\(token\)\}\/state/);
    expect(appSource).toMatch(/body: JSON\.stringify\(\{ active \}\)/);
  });

  it('distinguishes not-supported (409) from a load failure, localized', () => {
    expect(appSource).toMatch(/inputsResp\.status === 409/);
    expect(i18nSource).toContain("'io.notSupported':");
    expect(appSource).toContain("t('io.notSupported')");
  });

  it('escapes tokens when rendering the DI/DO tables', () => {
    expect(appSource).toMatch(/data-io-relay="\$\{esc\(r\.token\)\}"/);
  });

  it('wires the form and delegates relay clicks', () => {
    expect(appSource).toMatch(/\$\('io-form'\)\.addEventListener\('submit', loadIo\)/);
    expect(appSource).toMatch(/\$\('io-outputs'\)\.addEventListener\('click'/);
    expect(appSource).toMatch(/closest\('\[data-io-relay\]'\)/);
  });

  it('reports results through the live message region', () => {
    expect(indexSource).toMatch(/id="io-msg"[^>]*aria-live="polite"/);
  });
});
