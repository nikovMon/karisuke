// Run: node tests/ui/auto-run-ui.mjs
// Requires Playwright (npm install --no-save playwright) and a Chromium browser.
// PLAYWRIGHT_MODULE and PLAYWRIGHT_CHANNEL can point to an existing local install.
import http from 'node:http';
import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { fileURLToPath, pathToFileURL } from 'node:url';

const playwright = await import(process.env.PLAYWRIGHT_MODULE
  ? pathToFileURL(process.env.PLAYWRIGHT_MODULE).href : 'playwright');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../apps/rules-api/wwwroot/ui');
const screenshots = await fs.mkdtemp(path.join(os.tmpdir(), 'auto-run-ui-qa-'));
const options = {
  enabled: true, defaults: { publication: { batchSize: 10, delayMs: 1000 }, minOverlays: 0 },
  limits: { batchSize: { min: 1, max: 100 }, delayMs: { min: 0, max: 60000 }, maxSelectedOverlays: 100000,
    maxScannedRecords: 1000000, maxCandidates: 200000, payloadBudgetBytes: 536870912,
    readyLifetimeMinutes: 30, summaryLifetimeMinutes: 60, maxTargets: 100 },
  providerTimeZone: 'Asia/Jerusalem', singleInstance: true
};
const rules = Array.from({ length: 11 }, (_, i) => ({
  _id: `rule-${i + 1}`, ruleName: i === 0 ? 'Urban detail' : i === 1 ? 'Archive coverage' : `Rule ${i + 1}`,
  isActive: i !== 1, matchAll: false,
  match: { sensors: [{ name: 'SENSOR_A', registrationQualities: ['Accurate'], gridTypes: ['grid'] }],
    resolution: { min: 0.3, max: 1 }, maxPhotoAgeDays: 7 }, runParams: {}
}));
const requests = [];
const runs = new Map();
const reads = new Map();
let runSequence = 0;
function makeRun(body) {
  const now = new Date();
  const run = {
    id: `run-${++runSequence}`, state: 'Preparing', createdAt: now.toISOString(),
    readyAt: null, startedAt: null, finishedAt: null, expiresAt: new Date(+now + 1800000).toISOString(),
    sourceMode: body.source.mode, minOverlays: body.minOverlays, maxOverlays: body.maxOverlays ?? 200,
    shortfall: 0, counters: { scanned: 162, invalid: 2, duplicates: 4, observedMatches: 80, selected: 25,
      confirmed: 0, failed: 0, uncertain: 0, inFlight: 0, remaining: 25 },
    targets: body.targets.map(t => ({ ...t, ruleName: rules.find(r => r._id === t.ruleId).ruleName,
      isActive: rules.find(r => r._id === t.ruleId).isActive, selected: 20, confirmed: 0,
      failed: 0, uncertain: 0, shortfall: Math.max(0, t.minOverlays - 20),
      rule: rules.find(r => r._id === t.ruleId) })),
    publication: body.publication, activePublication: body.publication, publicationChangePending: false,
    sourceExhausted: false, stoppedReason: 'Scan limit',
    warnings: ['Observed counts cover scanned candidates only.'], errors: [],
    queries: [{ filter: { logical_operators: { AND: [{ match: { SENSOR: { type: 'IN', values: ['SENSOR_A'] } } }] } } }],
    confirmedPerSecond: 0
  };
  runs.set(run.id, run);
  return run;
}
const server = http.createServer(async (req, res) => {
  try {
    const url = new URL(req.url, 'http://localhost');
    let body;
    if (['POST', 'PATCH'].includes(req.method)) {
      const chunks = []; for await (const chunk of req) chunks.push(chunk);
      body = chunks.length ? JSON.parse(Buffer.concat(chunks)) : undefined;
    }
    requests.push({ method: req.method, path: url.pathname, search: url.search, body });
    const json = (data, status = 200) => { res.writeHead(status, { 'Content-Type': 'application/json' }); res.end(JSON.stringify(data)); };
    if (url.pathname === '/auto-runs/options') return json(options);
    if (url.pathname === '/pipelines') return json([
      { pipelineId: 'imagery', enabled: true, contractId: 'overlay' },
      { pipelineId: 'disabled-pipeline', enabled: false, contractId: 'overlay' }
    ]);
    if (url.pathname === '/pipelines/imagery/rules')
      return json(rules.slice(Number(url.searchParams.get('from')), Number(url.searchParams.get('from')) + Number(url.searchParams.get('size'))));
    if (url.pathname.startsWith('/pipelines/imagery/rules/')) {
      const rule = rules.find(r => r._id === decodeURIComponent(url.pathname.split('/').pop()));
      return json(rule ?? { error: 'Rule not found.' }, rule ? 200 : 404);
    }
    if (url.pathname === '/auto-runs/preview') return json(makeRun(body), 202);
    if (url.pathname === '/auto-runs') return json([...runs.values()]);
    const match = url.pathname.match(/^\/auto-runs\/([^/]+)(?:\/(items|start|cancel|publication))?$/);
    if (match) {
      const run = runs.get(match[1]);
      if (!run) return json({ error: 'Run not found.' }, 404);
      if (!match[2]) {
        reads.set(run.id, (reads.get(run.id) ?? 0) + 1);
        if (run.state === 'Preparing') { run.state = 'Ready'; run.readyAt = new Date().toISOString(); }
        return json(run);
      }
      if (match[2] === 'items') {
        let items = Array.from({ length: 25 }, (_, i) => ({
          overlayId: `overlay-${String(i + 1).padStart(3, '0')}`, sensorName: 'SENSOR_A',
          photoTime: '2026-10-19T10:00:00Z', targets: run.targets.map(t => ({ pipelineId: t.pipelineId, ruleId: t.ruleId })),
          outcome: i === 1 ? 'Failed' : i === 2 ? 'Uncertain' : run.state === 'Publishing' ? 'Confirmed' : 'Pending',
          error: i === 1 ? '<img src=x onerror="window.injected=true"> mock broker error' : i === 2 ? 'Acknowledgment not received.' : null
        }));
        const outcome = url.searchParams.get('outcome');
        if (outcome) items = items.filter(i => i.outcome === outcome);
        const from = Number(url.searchParams.get('from')), size = Number(url.searchParams.get('size'));
        return json({ from, size, total: items.length, items: items.slice(from, from + size) });
      }
      if (match[2] === 'start') {
        run.state = 'Publishing'; run.startedAt = new Date().toISOString();
        run.counters.confirmed = 15; run.counters.failed = 1; run.counters.uncertain = 1; run.counters.remaining = 8;
        run.targets.forEach(t => { t.confirmed = 12; t.failed = 1; t.uncertain = 1; });
        run.confirmedPerSecond = 5;
      }
      if (match[2] === 'cancel') { run.state = 'Cancelled'; run.finishedAt = new Date().toISOString(); }
      if (match[2] === 'publication') { run.publication = body; run.publicationChangePending = run.state === 'Publishing'; }
      return json(run);
    }
    if (url.pathname === '/favicon.ico') { res.writeHead(204); return res.end(); }
    const relative = url.pathname.replace(/^\/ui\/?/, '');
    const file = relative === '' || !path.extname(relative) ? 'index.html' : relative;
    const full = path.resolve(root, file);
    if (!full.startsWith(root + path.sep)) { res.writeHead(404); return res.end(); }
    const mime = { '.js': 'text/javascript', '.css': 'text/css', '.html': 'text/html' }[path.extname(file)];
    res.writeHead(200, { 'Content-Type': mime ?? 'text/plain' });
    res.end(await fs.readFile(full));
  } catch (error) { res.writeHead(500); res.end(String(error)); }
});
await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
const origin = `http://127.0.0.1:${server.address().port}`;
let browser;
try {
  browser = await playwright.chromium.launch({ headless: true, ...(process.env.PLAYWRIGHT_CHANNEL ? { channel: process.env.PLAYWRIGHT_CHANNEL } : {}) });
  const context = await browser.newContext({ viewport: { width: 1440, height: 1000 }, timezoneId: 'Asia/Jerusalem' });
  const page = await context.newPage();
  const pageErrors = [];
  page.on('pageerror', error => pageErrors.push(error.message));
  await page.goto(`${origin}/ui`);
  await page.locator('#prepare-button:not(:disabled)').waitFor();
  for (const [name, batch, delay] of [['Sequential', '1', '1000'], ['Burst', '100', '0'], ['Balanced', '10', '1000']]) {
    const preset = page.locator('#publication-presets').getByRole('button', { name: new RegExp(`^${name}:`) });
    await preset.click();
    assert.equal(await page.locator('#batch-size').inputValue(), batch);
    assert.equal(await page.locator('#delay-ms').inputValue(), delay);
    assert.equal(await preset.getAttribute('aria-pressed'), 'true');
  }
  assert.equal(requests.filter(r => r.method === 'POST' || r.method === 'PATCH').length, 0, 'configuration presets only edit the draft');
  await page.screenshot({ path: path.join(screenshots, 'configuration-desktop.png'), fullPage: true });
  await page.locator('#pipeline-select').selectOption('imagery');
  await page.locator('[name="rule-choice"]').first().waitFor();
  assert.equal(await page.locator('[name="rule-choice"]').count(), 10);
  await page.locator('#rules-next').click();
  await page.getByText('Rule 11', { exact: true }).waitFor();
  assert.equal(await page.locator('[name="rule-choice"]').count(), 1);
  await page.locator('#rules-prev').click();
  await page.getByText('Urban detail', { exact: true }).waitFor();
  await page.locator('[name="rule-choice"]').nth(0).check();
  await page.locator('[name="rule-choice"]').nth(1).check();
  await page.locator('#add-rules').click();
  assert.equal(await page.locator('[data-target-field="maxOverlays"]').count(), 2);
  await page.getByLabel('Minimum for Urban detail').fill('25');
  await page.getByLabel('Maximum for Urban detail').fill('30');
  await page.locator('[name="source-mode"][value="Filters"]').check();
  await page.locator('#sensor-names').fill('SENSOR_A, SENSOR_A');
  await page.locator('#is-realtime').selectOption('false');
  await page.locator('#resolution-min').fill('0.3');
  await page.locator('#resolution-max').fill('0.3');
  await page.locator('#input-timezone').selectOption('utc');
  await page.locator('#created-from').fill('2026-10-01T09:30');
  await page.locator('#photo-to').fill('2026-10-20T12:00');
  await page.locator('#area-wkt').fill('POLYGON ((34 31, 35 31, 35 32, 34 31))');
  await page.locator('#prepare-button').click();
  await page.waitForURL('**/ui/auto-run/runs/run-1');
  await page.locator('#start-run:not([hidden])').waitFor();
  const preview = requests.find(r => r.path === '/auto-runs/preview').body;
  assert.equal(preview.source.mode, 'Filters');
  assert.equal(preview.source.filters.isRealtime, false);
  assert.deepEqual(preview.source.filters.sensorNames, ['SENSOR_A']);
  assert.equal(preview.source.filters.createdTime.from, '2026-10-01T09:30:00.000Z');
  assert.deepEqual(preview.source.filters.resolution, { min: 0.3, max: 0.3 });
  assert.equal(preview.targets[0].minOverlays, 25);
  assert.equal(preview.targets[1].ruleId, 'rule-2');
  assert.equal(await page.locator('#count-selected').textContent(), '25');
  await page.locator('#items-next').click();
  await page.getByText('overlay-025', { exact: true }).waitFor();
  await page.locator('#items-prev').click();
  await page.getByText('overlay-001', { exact: true }).waitFor();
  await page.locator('#item-outcome').selectOption('Failed');
  await page.getByText('<img src=x onerror="window.injected=true"> mock broker error', { exact: true }).waitFor();
  assert.equal(await page.locator('#run-items img').count(), 0);
  assert.equal(await page.evaluate(() => window.injected), undefined);
  await page.locator('#item-outcome').selectOption('');
  await page.locator('#start-run').click();
  await page.locator('#run-state').getByText('Publishing', { exact: true }).waitFor();
  for (const [name, batch, delay] of [['Sequential', '1', '1000'], ['Balanced', '10', '1000'], ['Burst', '100', '0']]) {
    await page.locator('#run-publication-presets').getByRole('button', { name: new RegExp(`^${name}:`) }).click();
    assert.equal(await page.locator('#run-batch-size').inputValue(), batch);
    assert.equal(await page.locator('#run-delay-ms').inputValue(), delay);
  }
  await page.waitForTimeout(2200);
  assert.equal(await page.locator('#run-batch-size').inputValue(), '100', 'polling must preserve an unapplied preset');
  assert.equal(requests.filter(r => r.method === 'PATCH').length, 0, 'monitor presets require an explicit Apply settings');
  await page.locator('#run-batch-size').fill('6');
  assert.equal(await page.locator('#run-publication-presets [aria-pressed="true"]').count(), 0, 'manual values remain editable');
  await page.locator('.frozen-rule summary').first().click();
  await page.waitForTimeout(2200);
  assert.equal(await page.locator('#run-batch-size').inputValue(), '6', 'polling must preserve unsaved publication inputs');
  assert.ok(await page.locator('.frozen-rule').first().evaluate(element => element.open), 'polling must preserve expanded frozen conditions');
  await page.locator('#run-delay-ms').fill('1500');
  await page.locator('#update-publication').click();
  await page.getByText('Pending next batch · active: 10 overlays / 1000 ms', { exact: true }).waitFor();
  await page.screenshot({ path: path.join(screenshots, 'publishing-desktop.png'), fullPage: true });
  assert.deepEqual(requests.find(r => r.method === 'PATCH').body, { batchSize: 6, delayMs: 1500 });
  await page.locator('#cancel-run').click();
  await page.locator('#run-state').getByText('Cancelled', { exact: true }).waitFor();
  assert.equal(await page.locator('#run-publication-presets button:disabled').count(), 3, 'terminal jobs disable every preset');
  await page.waitForTimeout(150);
  const stoppedReads = reads.get('run-1');
  await page.waitForTimeout(2300);
  assert.equal(reads.get('run-1'), stoppedReads, 'terminal jobs must stop polling');
  await page.getByText('← Back to configuration', { exact: true }).click();
  assert.equal(await page.locator('#sensor-names').inputValue(), 'SENSOR_A, SENSOR_A');
  assert.equal(await page.getByLabel('Minimum for Urban detail').inputValue(), '25');
  await page.locator('[name="source-mode"][value="OverlayIds"]').check();
  await page.locator('#overlay-ids').fill('one, two\none');
  await page.locator('#prepare-button').click();
  await page.waitForURL('**/ui/auto-run/runs/run-2');
  await page.locator('#start-run:not([hidden])').waitFor();
  const idPreview = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(idPreview.source, { mode: 'OverlayIds', overlayIds: ['one', 'two'] });
  await page.reload();
  await page.locator('#start-run:not([hidden])').waitFor();
  assert.equal(await page.locator('#count-selected').textContent(), '25', 'deep links must reload');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.screenshot({ path: path.join(screenshots, 'run-mobile.png'), fullPage: true });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'run page must not overflow horizontally');
  await page.getByText('← Back to configuration', { exact: true }).click();
  await page.screenshot({ path: path.join(screenshots, 'configuration-mobile.png'), fullPage: true });
  assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'configuration page must not overflow horizontally');
  await page.goto(`${origin}/ui/auto-run/runs/missing-run`);
  await page.locator('#run-error').waitFor({ state: 'visible' });
  assert.match(await page.locator('#run-error').textContent(), /expired|restarted/);
  await page.getByText('← Back to configuration', { exact: true }).click();
  await page.locator('#pipeline-select').selectOption('imagery');
  await page.locator('.paste-rules summary').click();
  await page.locator('#pasted-rule-ids').fill('rule-11, missing-rule');
  await page.locator('#add-pasted-rules').click();
  await page.getByLabel('Maximum for Rule 11').waitFor();
  await page.locator('#rule-error').waitFor({ state: 'visible' });
  assert.match(await page.locator('#rule-error').textContent(), /missing-rule/);
  await page.locator('#prepare-button').click();
  await page.waitForURL('**/ui/auto-run/runs/run-3');
  await page.locator('#start-run:not([hidden])').waitFor();
  assert.deepEqual(requests.filter(r => r.path === '/auto-runs/preview').at(-1).body.source, { mode: 'Rules' });
  await page.getByText('← Back to configuration', { exact: true }).click();
  await page.locator('[name="source-mode"][value="Filters"]').check();
  await page.locator('#input-timezone').selectOption('local');
  await page.locator('#created-from').fill('2026-10-25T01:30');
  await page.locator('#prepare-button').click();
  await page.locator('#form-error').waitFor({ state: 'visible' });
  assert.match(await page.locator('#form-error').textContent(), /ambiguous/);
  assert.equal(requests.filter(r => r.path === '/auto-runs/preview').length, 3, 'ambiguous DST time must not submit');
  await page.goto(`${origin}/ui/unknown`);
  await page.getByText('Open auto-run', { exact: true }).click();
  await page.locator('#prepare-button:not(:disabled)').waitFor();
  options.enabled = false;
  const disabledPage = await context.newPage();
  disabledPage.on('pageerror', error => pageErrors.push(error.message));
  await disabledPage.goto(`${origin}/ui/auto-run`);
  await disabledPage.locator('#global-notice').waitFor({ state: 'visible' });
  assert.ok(await disabledPage.locator('#prepare-button').isDisabled(), 'disabled feature must not submit');
  assert.match(await disabledPage.locator('#global-notice').textContent(), /disabled/);
  await disabledPage.close();
  assert.deepEqual(pageErrors, [], 'no unhandled browser errors');
  console.log(JSON.stringify({ result: 'PASS', scenarios: ['rule pagination and inactive selection', 'typed filters and UTC dates',
    'preview and frozen selection', 'item paging and outcome filters', 'HTML escaping', 'publication inputs survive polling',
    'live batch update', 'cancel and terminal polling stop', 'draft preserved across navigation', 'ID deduplication',
    'deep-link reload', 'mobile overflow', 'expired or missing run', 'pasted IDs with partial lookup failures',
    'rule-derived source', 'ambiguous DST validation', 'unknown-route recovery', 'disabled feature',
    'configuration publication presets', 'monitor presets require Apply and survive polling', 'terminal preset controls disabled'], screenshots }, null, 2));
} finally {
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
