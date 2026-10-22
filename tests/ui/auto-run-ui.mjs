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
  enabled: true, defaults: { publication: { batchSize: 10, delayMs: 1000 }, minOverlays: 0, filterMaxOverlays: 100 },
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
const pipelineRules = new Map([
  ['imagery', rules],
  ['secondary', rules.slice(0, 10).map(rule => ({ ...rule, ruleName: `Secondary ${rule.ruleName}` }))],
  ['duplicates', [...rules.slice(0, 10), rules[0], rules[10]]],
  ['ninety', Array.from({ length: 90 }, (_, i) => ({ ...rules[0], _id: `ninety-${i}`, ruleName: `Ninety ${i}` }))],
  ['too-many', Array.from({ length: 101 }, (_, i) => ({ ...rules[0], _id: `many-${i}`, ruleName: `Many ${i}` }))],
  ['empty', []]
]);
const rulePageOverrides = new Map();
const ruleLookupGates = new Map();
const pendingResponses = new Set();
function holdRulePage(pipeline, offset) {
  let unblock, markEntered;
  const held = new Promise(resolve => { unblock = resolve; });
  const entered = new Promise(resolve => { markEntered = resolve; });
  const release = () => { unblock(); pendingResponses.delete(release); };
  pendingResponses.add(release);
  rulePageOverrides.set(pipeline, async (from, size) => {
    if (from === offset) { markEntered(); await held; }
    return { data: pipelineRules.get(pipeline).slice(from, from + size) };
  });
  return { entered, release };
}
const requests = [];
const runs = new Map();
const previewRequests = new Map();
let rerunFailure = false;
let rerunGate = null;
const reads = new Map();
let runSequence = 0;
let pipelineFailure = false;
let pipelineGate = null;
function makeRun(body) {
  const now = new Date();
  const directIds = body.source.mode === 'OverlayIds' && !body.targets?.length;
  const direct = ['OverlayIds', 'Filters'].includes(body.source.mode) && !body.targets?.length;
  const selected = directIds ? Math.min(body.source.overlayIds.length, body.maxOverlays ?? body.source.overlayIds.length) : 25;
  const run = {
    id: `run-${++runSequence}`, state: 'Preparing', createdAt: now.toISOString(),
    readyAt: null, startedAt: null, finishedAt: null, expiresAt: new Date(+now + 1800000).toISOString(),
    sourceMode: body.source.mode, minOverlays: body.minOverlays, maxOverlays: body.maxOverlays ?? (directIds ? selected : direct ? options.defaults.filterMaxOverlays : 200),
    overlayIds: directIds ? body.source.overlayIds.slice(0, selected) : null,
    shortfall: 0, counters: { scanned: directIds ? body.source.overlayIds.length : 162, invalid: direct ? 0 : 2,
      duplicates: direct ? 0 : 4, observedMatches: direct ? 0 : 80, selected,
      confirmed: 0, failed: 0, uncertain: 0, inFlight: 0, remaining: selected },
    targets: (body.targets ?? []).map(t => ({ ...t, ruleName: rules.find(r => r._id === t.ruleId).ruleName,
      isActive: rules.find(r => r._id === t.ruleId).isActive, selected: 20, confirmed: 0,
      failed: 0, uncertain: 0, shortfall: Math.max(0, t.minOverlays - 20),
      rule: rules.find(r => r._id === t.ruleId) })),
    publication: body.publication, activePublication: body.publication, publicationChangePending: false,
    sourceExhausted: direct, stoppedReason: direct ? null : 'Scan limit',
    warnings: direct ? [] : ['Observed counts cover scanned candidates only.'], errors: [],
    queries: directIds ? [] : [{ filter: { logical_operators: { AND: [{ match: { SENSOR: { type: 'IN', values: ['SENSOR_A'] } } }] } } }],
    confirmedPerSecond: 0
  };
  runs.set(run.id, run);
  previewRequests.set(run.id, structuredClone(body));
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
    if (url.pathname === '/pipelines') {
      await pipelineGate;
      return pipelineFailure ? json({ error: 'Pipeline discovery is unavailable.' }, 503) : json([
        ...[...pipelineRules.keys()].map(pipelineId => ({ pipelineId, enabled: true, contractId: 'overlay' })),
        { pipelineId: 'disabled-pipeline', enabled: false, contractId: 'overlay' }
      ]);
    }
    const rulePage = url.pathname.match(/^\/pipelines\/([^/]+)\/rules$/);
    if (rulePage) {
      const pipeline = decodeURIComponent(rulePage[1]);
      const from = Number(url.searchParams.get('from')), size = Number(url.searchParams.get('size'));
      const override = rulePageOverrides.get(pipeline);
      if (override) {
        const result = await override(from, size);
        return json(result.data, result.status ?? 200);
      }
      return json((pipelineRules.get(pipeline) ?? []).slice(from, from + size));
    }
    if (url.pathname.startsWith('/pipelines/imagery/rules/')) {
      const id = decodeURIComponent(url.pathname.split('/').pop());
      await ruleLookupGates.get(id)?.();
      const rule = rules.find(r => r._id === id);
      return json(rule ?? { error: 'Rule not found.' }, rule ? 200 : 404);
    }
    if (url.pathname === '/auto-runs/preview') return json(makeRun(body), 202);
    if (url.pathname === '/auto-runs') return json([...runs.values()]);
    const match = url.pathname.match(/^\/auto-runs\/([^/]+)(?:\/(items|start|cancel|publication|rerun))?$/);
    if (match) {
      const run = runs.get(match[1]);
      if (!run) return json({ error: 'Run not found.' }, 404);
      if (!match[2]) {
        reads.set(run.id, (reads.get(run.id) ?? 0) + 1);
        if (run.state === 'Preparing') { run.state = 'Ready'; run.readyAt = new Date().toISOString(); }
        return json(run);
      }
      if (match[2] === 'rerun') {
        await rerunGate;
        if (rerunFailure) return json({ error: 'Another preparation is already active.' }, 409);
        return json(makeRun({ ...structuredClone(previewRequests.get(run.id)), publication: { ...run.publication } }), 202);
      }
      if (match[2] === 'items') {
        let items = Array.from({ length: run.counters.selected }, (_, i) => ({
          overlayId: run.overlayIds?.[i] ?? `overlay-${String(i + 1).padStart(3, '0')}`, sensorName: 'SENSOR_A',
          photoTime: '2026-10-19T10:00:00Z', targets: run.targets.map(t => ({ pipelineId: t.pipelineId, ruleId: t.ruleId })),
          outcome: run.overlayIds ? (i < run.counters.confirmed ? 'Confirmed' : 'Pending')
            : i === 1 ? 'Failed' : i === 2 ? 'Uncertain' : run.state === 'Publishing' ? 'Confirmed' : 'Pending',
          error: run.overlayIds ? null : i === 1 ? '<img src=x onerror="window.injected=true"> mock broker error' : i === 2 ? 'Acknowledgment not received.' : null
        }));
        const outcome = url.searchParams.get('outcome');
        if (outcome) items = items.filter(i => i.outcome === outcome);
        const from = Number(url.searchParams.get('from')), size = Number(url.searchParams.get('size'));
        return json({ from, size, total: items.length, items: items.slice(from, from + size) });
      }
      if (match[2] === 'start') {
        run.state = 'Publishing'; run.startedAt = new Date().toISOString();
        run.counters.confirmed = run.overlayIds ? 1 : 15; run.counters.failed = run.overlayIds ? 0 : 1;
        run.counters.uncertain = run.overlayIds ? 0 : 1; run.counters.remaining = run.overlayIds ? run.counters.selected - 1 : 8;
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
  assert.equal(await page.title(), 'Auto Run · Flarules');
  await page.getByRole('link', { name: 'Flarules home' }).waitFor();
  await page.getByRole('link', { name: 'Auto Run 01', exact: true }).waitFor();
  await page.getByRole('link', { name: 'API Reference', exact: true }).waitFor();
  assert.equal(await page.locator('#input-timezone').count(), 0, 'timezone is automatic, not selectable');
  assert.equal(await page.locator('.environment-pill, .heading-mark, .sidebar-bottom, footer').count(), 0);
  assert.ok(!(await page.locator('body').textContent()).includes('HISTORICAL PROCESSING'));
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
  await page.locator('#filter-details summary').click();
  await page.locator('#sensor-names').fill('SENSOR_A, SENSOR_A');
  await page.locator('#is-realtime').selectOption('false');
  await page.locator('#resolution-min').fill('0.3');
  await page.locator('#resolution-max').fill('0.3');
  await page.locator('#created-from').fill('2026-10-01T09:30');
  await page.locator('#photo-to').fill('2026-10-20T12:00');
  await page.locator('#area-wkt').fill('POLYGON ((34 31, 35 31, 35 32, 34 31))');
  await page.locator('#prepare-button').click();
  await page.waitForURL('**/ui/auto-run/runs/run-1');
  await page.locator('#start-run:not([hidden])').waitFor();
  const preview = requests.find(r => r.path === '/auto-runs/preview').body;
  assert.equal(preview.source.mode, 'Rules');
  assert.equal(preview.source.filters.isRealtime, false);
  assert.deepEqual(preview.source.filters.sensorNames, ['SENSOR_A']);
  assert.equal(preview.source.filters.createdTime.from, '2026-10-01T06:30:00.000Z');
  assert.equal(preview.source.filters.photoTime.to, '2026-10-20T09:00:00.000Z');
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
  assert.ok(await page.locator('[name="id-processing"][value="direct"]').isChecked(), 'Overlay IDs defaults to direct gateway processing');
  assert.ok(await page.locator('#rule-selection').isHidden());
  assert.ok(await page.getByLabel('Minimum for Urban detail').isDisabled());
  await page.locator('[name="id-processing"][value="rules"]').check();
  assert.ok(await page.locator('#rule-selection').isVisible());
  assert.equal(await page.getByLabel('Minimum for Urban detail').inputValue(), '25');
  assert.equal(await page.getByLabel('Maximum for Urban detail').inputValue(), '30');
  await page.locator('[name="id-processing"][value="direct"]').check();
  await page.locator('#overlay-ids').fill('one, two\none');
  await page.locator('#prepare-button').click();
  await page.waitForURL('**/ui/auto-run/runs/run-2');
  await page.locator('#start-run:not([hidden])').waitFor();
  const idPreview = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(idPreview.source, { mode: 'OverlayIds', overlayIds: ['one', 'two'] });
  assert.deepEqual(idPreview.targets, [], 'direct mode must not include preserved hidden targets');
  assert.ok(await page.locator('#run-target-table').isHidden());
  assert.match(await page.locator('#direct-run-note').textContent(), /current rules, including photo-age limits/);
  assert.equal(await page.locator('#items-target-heading').textContent(), 'Processing');
  assert.ok(!(await page.locator('#run-meta').textContent()).includes('Observed matches'));
  await page.reload();
  await page.locator('#start-run:not([hidden])').waitFor();
  assert.equal(await page.locator('#count-selected').textContent(), '2', 'direct run deep links must reload');
  assert.ok(await page.locator('#direct-run-note').isVisible());
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
  await page.locator('#created-from').fill('2026-10-25T01:30');
  await page.locator('#prepare-button').click();
  await page.locator('#form-error').waitFor({ state: 'visible' });
  assert.match(await page.locator('#form-error').textContent(), /ambiguous/);
  assert.equal(requests.filter(r => r.path === '/auto-runs/preview').length, 3, 'ambiguous DST time must not submit');
  await page.goto(`${origin}/ui/unknown`);
  await page.getByText('Open auto-run', { exact: true }).click();
  await page.locator('#prepare-button:not(:disabled)').waitFor();

  const bulkPage = await context.newPage();
  bulkPage.on('pageerror', error => pageErrors.push(error.message));
  const targetRows = bulkPage.locator('#selected-targets tbody tr');
  const bulkButton = bulkPage.getByRole('button', { name: 'Select all rules', exact: true });
  const choosePipeline = async pipeline => {
    await bulkPage.locator('#pipeline-select').selectOption(pipeline);
    await bulkPage.locator('#rule-browser').waitFor({ state: 'visible' });
    await bulkPage.locator('#rule-list .loading').waitFor({ state: 'hidden' });
  };
  const openBulk = async pipeline => {
    await bulkPage.goto(`${origin}/ui/auto-run`);
    await bulkPage.locator('#prepare-button:not(:disabled)').waitFor();
    assert.ok(await bulkButton.isDisabled(), 'select all needs a selected pipeline');
    await choosePipeline(pipeline);
  };
  const selectAll = async status => {
    await bulkButton.click();
    await bulkPage.locator('#rule-selection-status').filter({ hasText: status }).waitFor();
    assert.ok(await bulkButton.isEnabled(), 'bulk selection must restore controls');
    assert.equal(await bulkButton.getAttribute('aria-busy'), 'false');
  };
  const addFirstRule = async () => {
    await bulkPage.locator('[name="rule-choice"]').first().check();
    await bulkPage.locator('#add-rules').click();
  };

  // Start on page two: selection still begins at page zero, preserving existing quotas.
  await openBulk('imagery');
  await addFirstRule();
  await bulkPage.getByLabel('Minimum for Urban detail').fill('7');
  await bulkPage.getByLabel('Maximum for Urban detail').fill('17');
  await bulkPage.locator('#rules-next').click();
  await bulkPage.getByText('Rule 11', { exact: true }).waitFor();
  const bulkStart = requests.length;
  await bulkButton.focus();
  await bulkPage.keyboard.press('Enter');
  await bulkPage.locator('#rule-selection-status').filter({ hasText: 'Added 10 rules from imagery.' }).waitFor();
  assert.equal(await targetRows.count(), 11, 'select all includes rules beyond the visible page');
  assert.equal(await bulkPage.getByLabel('Minimum for Urban detail').inputValue(), '7');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail').inputValue(), '17');
  assert.match(await targetRows.filter({ hasText: 'Archive coverage' }).textContent(), /inactive/);
  assert.deepEqual(requests.slice(bulkStart).filter(r => r.path === '/pipelines/imagery/rules')
    .map(r => Number(new URLSearchParams(r.search).get('from'))), [0, 10]);
  assert.match(await bulkPage.locator('#select-all-scope').textContent(), /imagery.*every page/);
  await bulkButton.focus();
  await bulkPage.keyboard.press('Space');
  await bulkPage.locator('#rule-selection-status').filter({ hasText: 'All 11 rules in imagery are already selected.' }).waitFor();
  assert.equal(await targetRows.count(), 11, 'repeated selection cannot duplicate targets');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail').inputValue(), '17');
  await bulkPage.locator('#pipeline-select').scrollIntoViewIfNeeded();
  await bulkPage.screenshot({ path: path.join(screenshots, 'select-all-desktop.png') });

  await choosePipeline('secondary');
  await selectAll('Added 10 rules from secondary.');
  assert.equal(await targetRows.count(), 21, 'the same rule IDs in different pipelines remain distinct targets');
  await choosePipeline('ninety');
  await bulkButton.click();
  await bulkPage.locator('#rule-error').filter({ hasText: '100-target limit across all pipelines' }).waitFor();
  assert.equal(await targetRows.count(), 21, 'over-capacity selection must be atomic across all pipelines');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail', { exact: true }).inputValue(), '17');
  assert.equal(await bulkPage.locator('#rule-selection-status').textContent(), '');
  assert.ok(await bulkButton.isEnabled());
  assert.ok(await bulkPage.locator('#prepare-button').isEnabled());
  await choosePipeline('empty');
  await selectAll('No rules found in empty.');
  assert.equal(await targetRows.count(), 21, 'an empty pipeline cannot remove existing targets');
  assert.ok(await bulkPage.locator('#rule-error').isHidden(), 'changing pipeline clears stale errors');
  await bulkPage.setViewportSize({ width: 390, height: 844 });
  await bulkPage.locator('#pipeline-select').scrollIntoViewIfNeeded();
  await bulkPage.screenshot({ path: path.join(screenshots, 'select-all-mobile.png') });
  assert.ok(await bulkPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'bulk selection controls must fit mobile');
  await bulkButton.focus();
  await bulkPage.keyboard.press('Enter');
  await bulkPage.locator('#rule-selection-status').filter({ hasText: 'No rules found in empty.' }).waitFor();
  assert.equal(await targetRows.count(), 21, 'mobile keyboard activation preserves existing selections');
  await bulkPage.setViewportSize({ width: 1440, height: 1000 });

  await openBulk('duplicates');
  await selectAll('Added 11 rules from duplicates.');
  assert.equal(await targetRows.count(), 11, 'duplicate records across pages count once within a pipeline');
  await openBulk('too-many');
  await bulkButton.click();
  await bulkPage.locator('#rule-error').filter({ hasText: '100-target limit' }).waitFor();
  assert.equal(await targetRows.count(), 0, 'a pipeline with 101 rules must not be silently truncated to 100');
  await choosePipeline('ninety');
  await selectAll('Added 90 rules from ninety.');
  await choosePipeline('secondary');
  await selectAll('Added 10 rules from secondary.');
  assert.equal(await targetRows.count(), 100, 'exactly the server target limit is supported');
  await selectAll('All 10 rules in secondary are already selected.');
  assert.equal(await targetRows.count(), 100, 'existing selections can be reselected at full capacity');

  await openBulk('imagery');
  await addFirstRule();
  await bulkPage.getByLabel('Minimum for Urban detail').fill('9');
  await bulkPage.getByLabel('Maximum for Urban detail').fill('19');
  rulePageOverrides.set('imagery', async (from, size) => from === 10
    ? { data: { error: 'The second page is temporarily unavailable.' }, status: 503 }
    : { data: rules.slice(from, from + size) });
  await bulkButton.click();
  await bulkPage.locator('#rule-error').filter({ hasText: 'No rules were added. The second page is temporarily unavailable.' }).waitFor();
  assert.equal(await targetRows.count(), 1, 'a later page failure cannot leave partial selection');
  assert.equal(await bulkPage.getByLabel('Minimum for Urban detail').inputValue(), '9');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail').inputValue(), '19');
  assert.ok(await bulkPage.locator('#add-rules').isEnabled());
  assert.ok(await bulkPage.locator('#add-pasted-rules').isEnabled());
  rulePageOverrides.delete('imagery');
  await selectAll('Added 10 rules from imagery.');
  assert.ok(await bulkPage.locator('#rule-error').isHidden(), 'retry clears the previous failure');

  await openBulk('imagery');
  await addFirstRule();
  const heldSelection = holdRulePage('imagery', 10);
  const raceStart = requests.length;
  await bulkButton.click();
  await heldSelection.entered;
  assert.equal(await targetRows.count(), 1, 'no rules are appended while later pages are pending');
  assert.ok(await bulkButton.isDisabled());
  assert.equal(await bulkButton.getAttribute('aria-busy'), 'true');
  for (const id of ['add-rules', 'add-pasted-rules', 'prepare-button'])
    assert.ok(await bulkPage.locator(`#${id}`).isDisabled(), `${id} must wait for selection`);
  await bulkPage.evaluate(() => {
    document.getElementById('select-all-rules').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    document.getElementById('add-pasted-rules').dispatchEvent(new MouseEvent('click', { bubbles: true }));
    document.getElementById('preview-form').dispatchEvent(new Event('submit', { bubbles: true, cancelable: true }));
  });
  await bulkPage.getByLabel('Minimum for Urban detail').fill('12');
  await bulkPage.getByLabel('Maximum for Urban detail').fill('34');
  heldSelection.release();
  rulePageOverrides.delete('imagery');
  await bulkPage.locator('#rule-selection-status').filter({ hasText: 'Added 10 rules from imagery.' }).waitFor();
  assert.equal(await bulkPage.getByLabel('Minimum for Urban detail').inputValue(), '12');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail').inputValue(), '34', 'edits during retrieval survive the atomic commit');
  assert.equal(requests.slice(raceStart).filter(r => r.path === '/auto-runs/preview').length, 0, 'preview cannot race pending selection');
  assert.equal(requests.slice(raceStart).filter(r => r.path === '/pipelines/imagery/rules').length, 2, 'repeated clicks cannot start concurrent bulk fetches');

  await openBulk('imagery');
  const staleSelection = holdRulePage('imagery', 10);
  await bulkButton.click();
  await staleSelection.entered;
  await choosePipeline('secondary');
  assert.equal(await bulkPage.locator('#rule-selection-status').textContent(), '', 'pipeline changes clear loading state');
  const nextSelection = holdRulePage('secondary', 0);
  await bulkButton.click();
  await nextSelection.entered;
  staleSelection.release();
  rulePageOverrides.delete('imagery');
  await bulkPage.waitForTimeout(100);
  assert.ok(await bulkButton.isDisabled(), 'stale completion cannot unlock a new operation');
  assert.match(await bulkPage.locator('#rule-selection-status').textContent(), /Loading all rules in secondary/);
  assert.equal(await targetRows.count(), 0, 'old pipeline results must never be committed');
  nextSelection.release();
  rulePageOverrides.delete('secondary');
  await bulkPage.locator('#rule-selection-status').filter({ hasText: 'Added 10 rules from secondary.' }).waitFor();
  assert.equal(await targetRows.count(), 10);
  assert.equal(await targetRows.filter({ hasText: 'imagery' }).count(), 0);
  assert.ok(await bulkPage.locator('#rule-error').isHidden(), 'cancelled requests must not show stale errors');

  await openBulk('imagery');
  let releaseLookup, markLookupEntered;
  const heldLookup = new Promise(resolve => { releaseLookup = resolve; });
  const lookupEntered = new Promise(resolve => { markLookupEntered = resolve; });
  pendingResponses.add(releaseLookup);
  ruleLookupGates.set('rule-6', async () => { markLookupEntered(); await heldLookup; });
  await bulkPage.locator('.paste-rules summary').click();
  await bulkPage.locator('#pasted-rule-ids').fill('rule-1, rule-2, rule-3, rule-4, rule-5, rule-6');
  await bulkPage.locator('#add-pasted-rules').click();
  await lookupEntered;
  assert.equal(await targetRows.count(), 5, 'completed pasted-ID batches must be visible while later batches are pending');
  assert.ok(await bulkPage.locator('#prepare-button').isDisabled());
  await bulkPage.getByLabel('Maximum for Urban detail').fill('42');
  await choosePipeline('secondary');
  assert.equal(await targetRows.count(), 5, 'cancelling pasted lookup keeps every committed target visible');
  assert.ok(await bulkPage.locator('#prepare-button').isEnabled());
  releaseLookup();
  pendingResponses.delete(releaseLookup);
  ruleLookupGates.delete('rule-6');
  await bulkPage.waitForTimeout(100);
  assert.equal(await targetRows.count(), 5, 'the cancelled second batch cannot add a hidden target');
  assert.equal(await bulkPage.getByLabel('Maximum for Urban detail').inputValue(), '42');
  assert.ok(await bulkPage.locator('#rule-error').isHidden());
  await bulkPage.locator('#prepare-button').click();
  await bulkPage.waitForURL('**/ui/auto-run/runs/run-4');
  await bulkPage.locator('#start-run:not([hidden])').waitFor();
  const pastedPreview = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(pastedPreview.targets.map(target => [target.pipelineId, target.ruleId]),
    rules.slice(0, 5).map(rule => ['imagery', rule._id]), 'submitted targets must match the five visible committed rules');
  assert.equal(pastedPreview.targets[0].maxOverlays, 42);
  await bulkPage.close();

  const directPage = await context.newPage();
  directPage.on('pageerror', error => pageErrors.push(error.message));
  await directPage.goto(`${origin}/ui/auto-run`);
  await directPage.locator('#prepare-button:not(:disabled)').waitFor();
  const directStart = requests.filter(r => r.path === '/auto-runs/preview').length;
  for (const mode of ['Rules']) {
    await directPage.locator(`[name="source-mode"][value="${mode}"]`).check();
    await directPage.locator('#prepare-button').click();
    await directPage.locator('#form-error').filter({ hasText: 'Select at least one pipeline rule' }).waitFor();
  }
  await directPage.locator('[name="source-mode"][value="OverlayIds"]').check();
  await directPage.locator('#prepare-button').click();
  await directPage.locator('#form-error').filter({ hasText: 'Enter at least one overlay ID' }).waitFor();
  assert.equal(requests.filter(r => r.path === '/auto-runs/preview').length, directStart, 'rules need targets; direct mode needs IDs');
  await directPage.locator('[name="id-processing"][value="rules"]').check();
  await directPage.locator('#pipeline-select').selectOption('imagery');
  await directPage.locator('[name="rule-choice"]').first().check();
  await directPage.locator('#add-rules').click();
  await directPage.getByLabel('Maximum for Urban detail').fill('13');
  await directPage.locator('#overlay-ids').fill('matched-one');
  await directPage.locator('#prepare-button').click();
  await directPage.waitForURL('**/ui/auto-run/runs/run-5');
  await directPage.locator('#start-run:not([hidden])').waitFor();
  const matchedIds = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(matchedIds.source, { mode: 'OverlayIds', overlayIds: ['matched-one'] });
  assert.deepEqual(matchedIds.targets, [{ pipelineId: 'imagery', ruleId: 'rule-1', minOverlays: 0, maxOverlays: 13 }]);
  assert.equal(await directPage.locator('#run-target-heading').textContent(), 'Rule assignments');
  assert.ok(await directPage.locator('#run-target-table').isVisible(), 'selected-rule IDs retain historical matching details');
  assert.ok(await directPage.locator('#direct-run-note').isHidden());
  await directPage.getByText('← Back to configuration', { exact: true }).click();
  await directPage.getByLabel('Maximum for Urban detail').fill('');
  await directPage.locator('[name="source-mode"][value="Filters"]').check();
  await directPage.locator('#resolution-min').fill('-1');
  await directPage.locator('[name="source-mode"][value="OverlayIds"]').check();
  await directPage.locator('[name="id-processing"][value="direct"]').check();
  assert.ok(await directPage.getByLabel('Maximum for Urban detail').isDisabled());
  assert.ok(await directPage.locator('#resolution-min').isDisabled());
  assert.ok(await directPage.locator('#preview-form').evaluate(form => form.checkValidity()), 'invalid hidden quotas and filters cannot block a direct run');
  await directPage.locator('#overlay-ids').fill('direct-one, direct-two, direct-three, direct-two');
  await directPage.locator('#overall-min').fill('1');
  await directPage.locator('#overall-max').fill('2');
  await directPage.locator('#batch-size').fill('7');
  await directPage.locator('#delay-ms').fill('1500');
  await directPage.evaluate(() => scrollTo(0, 0));
  await directPage.screenshot({ path: path.join(screenshots, 'direct-configuration-desktop.png'), fullPage: true });
  await directPage.setViewportSize({ width: 390, height: 844 });
  await directPage.screenshot({ path: path.join(screenshots, 'direct-configuration-mobile.png'), fullPage: true });
  assert.ok(await directPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'direct choice must fit mobile');
  await directPage.locator('#prepare-button').click();
  await directPage.waitForURL('**/ui/auto-run/runs/run-6');
  await directPage.locator('#start-run:not([hidden])').waitFor();
  const directPreview = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(directPreview.source, { mode: 'OverlayIds', overlayIds: ['direct-one', 'direct-two', 'direct-three'] });
  assert.deepEqual(directPreview.targets, []);
  assert.equal(directPreview.minOverlays, 1);
  assert.equal(directPreview.maxOverlays, 2);
  assert.deepEqual(directPreview.publication, { batchSize: 7, delayMs: 1500 });
  assert.equal(await directPage.locator('#count-selected').textContent(), '2');
  assert.equal(await directPage.locator('#run-items tr').count(), 2);
  assert.equal(await directPage.locator('#run-items').getByText('Gateway rules', { exact: true }).count(), 2);
  assert.match(await directPage.locator('#items-count').textContent(), /2 overlays/);
  assert.match(await directPage.locator('#direct-run-limits').textContent(), /minimum: 1 overlays.*Maximum: 2 overlays/);
  await directPage.screenshot({ path: path.join(screenshots, 'direct-run-mobile.png'), fullPage: true });
  assert.ok(await directPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'direct monitor must fit mobile');
  await directPage.setViewportSize({ width: 1440, height: 1000 });
  await directPage.evaluate(() => scrollTo(0, 0));
  await directPage.screenshot({ path: path.join(screenshots, 'direct-run-desktop.png'), fullPage: true });
  await directPage.locator('#start-run').click();
  await directPage.locator('#run-state').getByText('Publishing', { exact: true }).waitFor();
  assert.equal(await directPage.locator('#count-confirmed').textContent(), '1');
  await directPage.locator('#run-batch-size').fill('4');
  await directPage.locator('#run-delay-ms').fill('500');
  await directPage.locator('#update-publication').click();
  await directPage.getByText('Pending next batch · active: 7 overlays / 1500 ms', { exact: true }).waitFor();
  assert.deepEqual(requests.filter(r => r.path === '/auto-runs/run-6/publication').at(-1).body, { batchSize: 4, delayMs: 500 });
  await directPage.locator('#cancel-run').click();
  await directPage.locator('#run-state').getByText('Cancelled', { exact: true }).waitFor();
  await directPage.getByText('← Back to configuration', { exact: true }).click();
  await directPage.locator('[name="id-processing"][value="rules"]').check();
  assert.equal(await directPage.getByLabel('Maximum for Urban detail').inputValue(), '', 'rule drafts survive direct preview and monitoring');
  assert.ok(await directPage.getByLabel('Maximum for Urban detail').isEnabled());
  await directPage.getByLabel('Maximum for Urban detail').fill('13');
  await directPage.locator('[name="source-mode"][value="Filters"]').check();
  assert.equal(await directPage.locator('#resolution-min').inputValue(), '-1', 'filters remain in the draft when switching modes');
  assert.ok(await directPage.locator('#resolution-min').isEnabled());
  const historyDirect = directPage.locator('#history-list a[href="/ui/auto-run/runs/run-6"]');
  await historyDirect.getByText('Direct to gateway', { exact: true }).waitFor();
  await historyDirect.click();
  await directPage.locator('#direct-run-note').waitFor({ state: 'visible' });
  await directPage.reload();
  await directPage.locator('#direct-run-note').waitFor({ state: 'visible' });
  assert.equal(await directPage.locator('#run-target-heading').textContent(), 'Gateway processing');
  assert.equal(await directPage.locator('#run-items tr').count(), 2, 'direct history links reload overlay outcomes');
  await directPage.close();

  pipelineFailure = true;
  let releasePipelines;
  pipelineGate = new Promise(resolve => { releasePipelines = resolve; });
  pendingResponses.add(releasePipelines);
  const outagePage = await context.newPage();
  outagePage.on('pageerror', error => pageErrors.push(error.message));
  await outagePage.goto(`${origin}/ui/auto-run`);
  await outagePage.locator('#connection-state').filter({ hasText: 'API connected' }).waitFor();
  assert.ok(await outagePage.locator('#prepare-button').isDisabled(), 'rule-based preparation waits for pipeline discovery');
  await outagePage.locator('[name="source-mode"][value="OverlayIds"]').check();
  await outagePage.locator('#overlay-ids').fill('pending-pipeline-id');
  assert.ok(await outagePage.locator('#prepare-button').isEnabled(), 'direct preparation only waits for options, not pipelines');
  await outagePage.locator('#prepare-button').click();
  await outagePage.waitForURL('**/ui/auto-run/runs/run-7');
  await outagePage.locator('#start-run:not([hidden])').waitFor();
  releasePipelines();
  pendingResponses.delete(releasePipelines);
  pipelineGate = null;
  await outagePage.waitForFunction(() => document.getElementById('pipeline-select').textContent.includes('Could not load pipelines'));
  await outagePage.getByText('← Back to configuration', { exact: true }).click();
  await outagePage.locator('[name="id-processing"][value="rules"]').check();
  assert.ok(await outagePage.locator('#prepare-button').isDisabled());
  assert.match(await outagePage.locator('#rule-error').textContent(), /Pipeline discovery is unavailable/);
  await outagePage.locator('[name="id-processing"][value="direct"]').check();
  assert.ok(await outagePage.locator('#prepare-button').isEnabled(), 'pipeline failure cannot block a direct preview');
  await outagePage.locator('#overlay-ids').fill('unavailable-pipeline-id');
  await outagePage.locator('#prepare-button').click();
  await outagePage.waitForURL('**/ui/auto-run/runs/run-8');
  await outagePage.locator('#start-run:not([hidden])').waitFor();
  assert.deepEqual(requests.filter(r => r.path === '/auto-runs/preview').at(-1).body.targets, []);
  await outagePage.reload();
  await outagePage.locator('#direct-run-note').waitFor({ state: 'visible' });
  assert.equal(await outagePage.locator('#count-selected').textContent(), '1');
  await outagePage.close();
  pipelineFailure = false;

  // Filters are an independent source: retained target drafts cannot affect payload or validation.
  const filtersPage = await context.newPage();
  filtersPage.on('pageerror', error => pageErrors.push(error.message));
  await filtersPage.goto(`${origin}/ui/auto-run`);
  await filtersPage.locator('#prepare-button:not(:disabled)').waitFor();
  await filtersPage.locator('#pipeline-select').selectOption('imagery');
  await filtersPage.locator('[name="rule-choice"]').first().check();
  await filtersPage.locator('#add-rules').click();
  await filtersPage.getByLabel('Maximum for Urban detail').fill('');
  await filtersPage.locator('[name="source-mode"][value="Filters"]').check();
  assert.ok(await filtersPage.locator('#rule-selection').isHidden());
  assert.ok(await filtersPage.getByLabel('Maximum for Urban detail').isDisabled());
  assert.equal(await filtersPage.locator('#overall-max').getAttribute('placeholder'), 'Default: 100 overlays');
  assert.ok(await filtersPage.locator('#preview-form').evaluate(form => form.checkValidity()), 'hidden invalid rule quota cannot block filters');
  await filtersPage.locator('#sensor-names').fill('ar');
  await filtersPage.locator('#resolution-min').fill('1');
  await filtersPage.locator('#resolution-max').fill('20');
  await filtersPage.locator('#photo-from').fill('2026-10-04T19:40');
  await filtersPage.locator('#photo-to').fill('2026-10-05T19:40');
  await filtersPage.locator('#overall-min').fill('2');
  await filtersPage.locator('#overall-max').fill('25');
  await filtersPage.locator('#batch-size').fill('6');
  await filtersPage.locator('#delay-ms').fill('250');
  await filtersPage.evaluate(() => scrollTo(0, 0));
  await filtersPage.screenshot({ path: path.join(screenshots, 'flarules-filters-desktop.png'), fullPage: true });
  await filtersPage.setViewportSize({ width: 390, height: 844 });
  await filtersPage.evaluate(() => scrollTo(0, 0));
  await filtersPage.screenshot({ path: path.join(screenshots, 'flarules-filters-mobile.png'), fullPage: true });
  assert.ok(await filtersPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'filter-only configuration fits mobile');
  await filtersPage.locator('#prepare-button').click();
  await filtersPage.locator('#start-run:not([hidden])').waitFor();
  const filtersRunId = new URL(filtersPage.url()).pathname.split('/').pop();
  const filtersRequest = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(filtersRequest, {
    source: { mode: 'Filters', filters: { sensorNames: ['ar'], resolution: { min: 1, max: 20 },
      photoTime: { from: '2026-10-04T16:40:00.000Z', to: '2026-10-05T16:40:00.000Z' } } },
    targets: [], minOverlays: 2, maxOverlays: 25, publication: { batchSize: 6, delayMs: 250 }
  });
  assert.ok(await filtersPage.locator('#run-target-table').isHidden());
  assert.ok(await filtersPage.locator('#direct-run-note').isVisible());
  assert.match(await filtersPage.locator('#provider-queries').textContent(), /logical_operators/);
  assert.equal(await filtersPage.locator('#run-items').getByText('Gateway rules', { exact: true }).count(), 20);
  await filtersPage.locator('#start-run').click();
  await filtersPage.locator('#run-state').getByText('Publishing', { exact: true }).waitFor();
  assert.ok(await filtersPage.locator('#rerun-run').isHidden(), 'active jobs cannot be rerun');
  await filtersPage.locator('#run-batch-size').fill('9');
  await filtersPage.locator('#run-delay-ms').fill('500');
  await filtersPage.locator('#update-publication').click();
  await filtersPage.locator('#publication-status').filter({ hasText: 'Pending next batch' }).waitFor();
  await filtersPage.locator('#cancel-run').click();
  await filtersPage.locator('#run-state').getByText('Cancelled', { exact: true }).waitFor();
  await filtersPage.reload();
  await filtersPage.locator('#rerun-run:not([hidden])').waitFor();
  assert.equal(await filtersPage.title(), 'Run details · Flarules');
  const postsBeforeRerun = requests.filter(r => r.path.endsWith('/start')).length;
  let releaseRerun;
  rerunGate = new Promise(resolve => { releaseRerun = resolve; });
  pendingResponses.add(releaseRerun);
  await filtersPage.locator('#rerun-run').focus();
  await filtersPage.keyboard.press('Enter');
  await filtersPage.locator('#rerun-run:disabled').waitFor();
  await filtersPage.evaluate(() => document.getElementById('rerun-run').dispatchEvent(new MouseEvent('click', { bubbles: true })));
  assert.equal(requests.filter(r => r.path === `/auto-runs/${filtersRunId}/rerun`).length, 1, 'repeat clicks cannot create duplicate previews');
  releaseRerun();
  pendingResponses.delete(releaseRerun);
  rerunGate = null;
  await filtersPage.waitForURL(url => !url.pathname.endsWith(`/${filtersRunId}`));
  await filtersPage.locator('#start-run:not([hidden])').waitFor();
  const filtersRerunId = new URL(filtersPage.url()).pathname.split('/').pop();
  assert.notEqual(filtersRerunId, filtersRunId);
  assert.deepEqual(previewRequests.get(filtersRerunId), { ...filtersRequest, publication: { batchSize: 9, delayMs: 500 } });
  assert.equal(requests.filter(r => r.path.endsWith('/start')).length, postsBeforeRerun, 'rerun prepares only; Start remains explicit');
  assert.equal(await filtersPage.locator('#run-state').textContent(), 'Ready');
  await filtersPage.evaluate(() => scrollTo(0, 0));
  await filtersPage.screenshot({ path: path.join(screenshots, 'flarules-rerun-mobile.png'), fullPage: true });
  assert.ok(await filtersPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'rerun actions fit mobile');
  rerunFailure = true;
  await filtersPage.locator('#rerun-run').click();
  await filtersPage.locator('#run-error').filter({ hasText: 'Another preparation is already active.' }).waitFor();
  assert.ok(filtersPage.url().endsWith(`/${filtersRerunId}`), 'rerun failures keep the original run visible');
  assert.ok(await filtersPage.locator('#rerun-run').isEnabled(), 'rerun failure restores the action');
  rerunFailure = false;
  await filtersPage.locator('#rerun-run').click();
  await filtersPage.waitForURL(url => !url.pathname.endsWith(`/${filtersRerunId}`));
  await filtersPage.locator('#start-run:not([hidden])').waitFor();
  assert.ok(await filtersPage.locator('#run-error').isHidden());
  await filtersPage.close();

  // Rerun works from retained rule/ID runs after a full reload, without configuration drafts.
  const rerunPage = await context.newPage();
  rerunPage.on('pageerror', error => pageErrors.push(error.message));
  for (const sourceId of ['run-1', 'run-2']) {
    await rerunPage.goto(`${origin}/ui/auto-run/runs/${sourceId}`);
    await rerunPage.locator('#rerun-run:not([hidden])').waitFor();
    await rerunPage.locator('#rerun-run').click();
    await rerunPage.waitForURL(url => !url.pathname.endsWith(`/${sourceId}`));
    await rerunPage.locator('#start-run:not([hidden])').waitFor();
    const rerunId = new URL(rerunPage.url()).pathname.split('/').pop();
    assert.deepEqual(previewRequests.get(rerunId), { ...previewRequests.get(sourceId), publication: runs.get(sourceId).publication });
    assert.equal(await rerunPage.locator('#run-state').textContent(), 'Ready');
    assert.equal(await rerunPage.locator('#direct-run-note').isVisible(), sourceId === 'run-2');
  }
  await rerunPage.close();

  pipelineFailure = true;
  const filterOutagePage = await context.newPage();
  filterOutagePage.on('pageerror', error => pageErrors.push(error.message));
  await filterOutagePage.goto(`${origin}/ui/auto-run`);
  await filterOutagePage.locator('#connection-state').filter({ hasText: 'API connected' }).waitFor();
  await filterOutagePage.locator('[name="source-mode"][value="Filters"]').check();
  assert.ok(await filterOutagePage.locator('#prepare-button').isEnabled(), 'filters need no pipeline discovery');
  await filterOutagePage.locator('#prepare-button').click();
  await filterOutagePage.locator('#start-run:not([hidden])').waitFor();
  const broadFilters = requests.filter(r => r.path === '/auto-runs/preview').at(-1).body;
  assert.deepEqual(broadFilters.source, { mode: 'Filters' });
  assert.deepEqual(broadFilters.targets, []);
  assert.equal(broadFilters.maxOverlays, undefined, 'empty filters can use the bounded server default');
  assert.match(await filterOutagePage.locator('#direct-run-limits').textContent(), /Maximum: 100 overlays/);
  await filterOutagePage.close();
  pipelineFailure = false;

  options.enabled = false;
  const disabledPage = await context.newPage();
  disabledPage.on('pageerror', error => pageErrors.push(error.message));
  await disabledPage.goto(`${origin}/ui/auto-run`);
  await disabledPage.locator('#global-notice').waitFor({ state: 'visible' });
  assert.ok(await disabledPage.locator('#prepare-button').isDisabled(), 'disabled feature must not submit');
  assert.match(await disabledPage.locator('#global-notice').textContent(), /disabled/);
  await disabledPage.locator('[name="source-mode"][value="OverlayIds"]').check();
  assert.ok(await disabledPage.locator('#prepare-button').isDisabled(), 'disabled feature also prevents direct previews');
  await disabledPage.close();
  assert.deepEqual(pageErrors, [], 'no unhandled browser errors');
  console.log(JSON.stringify({ result: 'PASS', scenarios: ['rule pagination and inactive selection', 'typed filters and UTC dates',
    'preview and frozen selection', 'item paging and outcome filters', 'HTML escaping', 'publication inputs survive polling',
    'live batch update', 'cancel and terminal polling stop', 'draft preserved across navigation', 'ID deduplication',
    'deep-link reload', 'mobile overflow', 'expired or missing run', 'pasted IDs with partial lookup failures',
    'rule-derived source', 'ambiguous DST validation', 'unknown-route recovery', 'disabled feature',
    'configuration publication presets', 'monitor presets require Apply and survive polling', 'terminal preset controls disabled',
    'select all across every page including inactive rules', 'bulk keyboard and mobile controls', 'bulk duplicate and quota preservation',
    'bulk cross-pipeline identity and capacity', 'empty pipeline selection', 'bulk exact target limit and no truncation',
    'atomic page failure and retry', 'bulk operation locking and live quota edits', 'stale pipeline cancellation and operation isolation',
    'pasted-ID batch cancellation keeps committed targets visible and submitted',
    'Overlay IDs defaults to direct gateway mode with no hidden targets or filters', 'optional selected-rule matching for IDs',
    'direct mode disables hidden validation and preserves rule/filter drafts', 'direct preview limits and publication controls',
    'direct monitor, history, deep links and mobile layout', 'direct preparation survives pending and failed pipeline discovery',
    'rules mode requires targets and direct mode requires enabled options and IDs',
    'Flarules branding with reduced shell and automatic local-to-UTC timestamps',
    'independent filters omit hidden targets, validate times, preserve bounded limits and bypass pipeline discovery',
    'rerun restores original filters and latest publication settings without auto-publishing',
    'rerun duplicate-click protection, conflict recovery, keyboard and mobile layout',
    'rule and overlay-ID reruns work from retained deep links without configuration drafts'], screenshots }, null, 2));
} finally {
  for (const release of pendingResponses) release();
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
