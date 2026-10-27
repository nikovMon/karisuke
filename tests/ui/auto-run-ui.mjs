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
  singleInstance: true,
  execution: { activePreparations: 2, activePublications: 1, queuedPreparations: 1, queuedPublications: 1,
    maxConcurrentPreparations: 3, maxConcurrentPublications: 3, maxQueuedJobs: 20,
    providerMaxConcurrentRequests: 2, publisherConcurrency: 4, preparationReservationBytes: 402653184, reservedPreparationBytes: 805306368 },
  provider: { requestDelayMs: 250, maxRetries: 2, retryDelayMs: 1000,
    sensorTypeMappings: { VIS: 'VIS', PAN: 'PAN', 'Thermal imagery': 'IR', SAR: 'SAR' } },
  ui: { mapUrl: null },
  memory: { availableBytes: 12884901888, processBytes: 1073741824, managedBytes: 536870912,
    limitBytes: 10307921510, headroomBytes: 9234179686, isUnderPressure: false }
};
const sensorTypeLabel = '<img src=x onerror="window.sensorTypeInjected=true">';
options.provider.sensorTypeMappings[sensorTypeLabel] = 'TEST';
const rules = Array.from({ length: 11 }, (_, i) => ({
  _id: `rule-${i + 1}`, ruleName: i === 0 ? 'Urban detail' : i === 1 ? 'Archive coverage' : `Rule ${i + 1}`,
  isActive: i !== 1, matchAll: false,
  match: i === 1 ? { sensorTypes: ['EO', sensorTypeLabel] } : { sensors: [{ name: 'SENSOR_A', registrationQualities: ['Accurate'], gridTypes: ['grid'] }],
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
let startFailure = false;
let startGate = null;
let previewFailure = null;
const reads = new Map();
const runResponseOverrides = new Map();
let queueStart = false;
let runSequence = 0;
let pipelineFailure = false;
let pipelineGate = null;
let deletionFailure = false;
const pendingDeletions = new Map();
function deleteRetainedRuns(ids) {
  const result = { deletedIds: [], pendingIds: [], releasedRetainedBytes: 0 };
  for (const id of ids) {
    const run = runs.get(id);
    if (['Preparing', 'Publishing'].includes(run.state)) {
      run.deletionRequested = true;
      pendingDeletions.set(id, 1);
      result.pendingIds.push(id);
    } else {
      result.deletedIds.push(id);
      result.releasedRetainedBytes += run.retainedBytes;
      runs.delete(id);
    }
  }
  return result;
}
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
    confirmedPerSecond: 0, memory: structuredClone(options.memory), retainedBytes: 2048 + selected * 4096, deletionRequested: false
  };
  if (body.source.filters?.sensorTypes?.length) run.queries[0].filter.logical_operators.AND.push({ match: {
    'sensorType.keyword': { type: 'IN', values: body.source.filters.sensorTypes.map(type => options.provider.sensorTypeMappings[type]) }
  } });
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
    if (url.pathname === '/map-test') {
      res.writeHead(200, { 'Content-Type': 'text/html' });
      return res.end('<!doctype html><html><body><h1>Test area map</h1><button>Choose an area</button></body></html>');
    }
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
        const result = await override(from, size, url.searchParams);
        return json(result.data, result.status ?? 200);
      }
      const search = (url.searchParams.get('search') ?? '').toLowerCase();
      const active = url.searchParams.get('isActive');
      return json((pipelineRules.get(pipeline) ?? []).filter(rule =>
        (active === null || rule.isActive === (active === 'true')) &&
        (!search || [rule.ruleName, rule._id, rule.description, ...(rule.match?.sensors ?? []).map(sensor => sensor.name), ...(rule.match?.sensorTypes ?? [])]
          .some(value => String(value ?? '').toLowerCase().includes(search)))).slice(from, from + size));
    }
    if (url.pathname.startsWith('/pipelines/imagery/rules/')) {
      const id = decodeURIComponent(url.pathname.split('/').pop());
      await ruleLookupGates.get(id)?.();
      const rule = rules.find(r => r._id === id);
      return json(rule ?? { error: 'Rule not found.' }, rule ? 200 : 404);
    }
    if (url.pathname === '/auto-runs/preview') {
      if (previewFailure?.text !== undefined) {
        res.writeHead(previewFailure.status, { 'Content-Type': 'text/plain' });
        return res.end(previewFailure.text);
      }
      if (previewFailure) return json(previewFailure.body, previewFailure.status);
      return json(makeRun(body), 202);
    }
    if (url.pathname === '/auto-runs') {
      if (req.method === 'DELETE') {
        if (deletionFailure) return json({ error: 'Cleanup unavailable. Try again.' }, 503);
        const result = deleteRetainedRuns([...runs.keys()]);
        return json(result, result.pendingIds.length ? 202 : 200);
      }
      for (const [id, polls] of pendingDeletions) {
        if (polls === 0) { runs.delete(id); pendingDeletions.delete(id); }
        else pendingDeletions.set(id, polls - 1);
      }
      const summaries = [...runs.values()].map(run => {
        if (url.searchParams.get('summaryOnly') !== 'true') return run;
        const { targets, queries, diagnostics, warnings, errors, ...summary } = run;
        return { ...summary, isDirect: ['OverlayIds', 'Filters'].includes(run.sourceMode) && targets.length === 0 };
      });
      return json(summaries);
    }
    const match = url.pathname.match(/^\/auto-runs\/([^/]+)(?:\/(items|start|cancel|publication|rerun))?$/);
    if (match) {
      const run = runs.get(match[1]);
      if (!run) return json({ error: 'Run not found.' }, 404);
      if (!match[2]) {
        if (req.method === 'DELETE') {
          if (deletionFailure) return json({ error: 'Cleanup unavailable. Try again.' }, 503);
          const result = deleteRetainedRuns([run.id]);
          return json(result, result.pendingIds.length ? 202 : 200);
        }
        reads.set(run.id, (reads.get(run.id) ?? 0) + 1);
        if (run.state === 'Preparing' && !run.providerProgress) { run.state = 'Ready'; run.readyAt = new Date().toISOString(); }
        return json({ ...run, serverTime: new Date().toISOString(), ...runResponseOverrides.get(run.id)?.(run) });
      }
      if (match[2] === 'rerun') {
        await rerunGate;
        if (rerunFailure) return json({ error: 'Another preparation is already active.' }, 409);
        return json(makeRun({ ...structuredClone(previewRequests.get(run.id)), publication: { ...run.publication } }), 202);
      }
      if (match[2] === 'items') {
        let items = Array.from({ length: run.counters.selected }, (_, i) => ({
          overlayId: run.overlayIds?.[i] ?? `overlay-${String(i + 1).padStart(3, '0')}`, sensorName: 'SENSOR_A', sensorType: 'VIS',
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
        await startGate;
        if (startFailure) return json({ error: 'Another operation is active. Try starting this preview again.' }, 409);
        if (queueStart) {
          run.state = 'QueuedPublication'; run.startRequestedAt = new Date().toISOString();
          run.queuePosition = 2; run.waitReason = 'Waiting for a publication worker'; run.expiresAt = null;
          return json(run, 202);
        }
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
options.ui.mapUrl = `${origin}/map-test`;
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
  assert.equal(await page.locator('#grid-types, #sortie-ids').count(), 0, 'removed filters cannot enter the request');
  assert.equal(await page.locator('#area-map-frame').getAttribute('src'), null, 'map is not loaded before opening');
  assert.equal(requests.filter(r => r.path === '/map-test').length, 0);
  await page.locator('#filter-details summary').click();
  await page.locator('#open-area-map:not(:disabled)').focus();
  await page.keyboard.press('Enter');
  await page.getByRole('dialog', { name: 'Search area map' }).waitFor({ state: 'visible' });
  await page.frameLocator('#area-map-frame').getByRole('heading', { name: 'Test area map' }).waitFor();
  assert.equal(await page.locator('#area-map-frame').getAttribute('src'), options.ui.mapUrl);
  assert.equal(await page.locator('#area-map-external').getAttribute('href'), options.ui.mapUrl);
  assert.ok(await page.locator('#close-area-map').evaluate(element => document.activeElement === element), 'dialog opens with close action focused');
  await page.keyboard.press('Shift+Tab');
  assert.ok(await page.locator('#area-map-dialog').evaluate(dialog => dialog.contains(document.activeElement)), 'focus stays inside the modal');
  await page.screenshot({ path: path.join(screenshots, 'area-map-desktop.png'), fullPage: true });
  // The embedded site owns its keyboard events; test Escape in the enclosing dialog.
  await page.locator('#close-area-map').focus();
  await page.keyboard.press('Escape');
  await page.locator('#area-map-dialog').waitFor({ state: 'hidden' });
  await page.waitForFunction(() => !document.getElementById('area-map-frame').hasAttribute('src'));
  assert.equal(await page.locator('#area-map-frame').getAttribute('src'), null, 'closing unloads the map');
  assert.ok(await page.locator('#open-area-map').evaluate(element => document.activeElement === element), 'focus returns to the map trigger');
  await page.setViewportSize({ width: 390, height: 844 });
  await page.locator('#open-area-map').click();
  await page.frameLocator('#area-map-frame').getByRole('heading').waitFor();
  assert.ok(await page.locator('#area-map-dialog').evaluate(dialog => dialog.getBoundingClientRect().width <= innerWidth && dialog.scrollWidth <= dialog.clientWidth), 'map modal fits a mobile viewport');
  await page.screenshot({ path: path.join(screenshots, 'area-map-mobile.png'), fullPage: true });
  await page.locator('#close-area-map').click();
  await page.locator('#area-map-dialog').waitFor({ state: 'hidden' });
  assert.equal(await page.locator('#area-wkt').inputValue(), '', 'map never invents a WKT integration protocol');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.locator('#filter-details summary').click();
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
  const sensorTypeRule = page.locator('.rule-choice').filter({ hasText: 'Archive coverage' });
  assert.equal(await sensorTypeRule.locator('.rule-choice-meta').textContent(), `Sensor types: EO, ${sensorTypeLabel}`,
    'sensor-type-only rules show their condition instead of claiming no constraints');
  assert.equal(await sensorTypeRule.locator('img').count(), 0, 'sensor type labels are escaped in the rule browser');
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
  assert.equal(await page.locator('#sensor-types input[type="text"]').count(), 0, 'sensor types cannot be entered as arbitrary text');
  assert.deepEqual(await page.locator('#sensor-types input').evaluateAll(inputs => inputs.map(input => input.value)), Object.keys(options.provider.sensorTypeMappings));
  assert.equal(await page.locator('#sensor-types img').count(), 0, 'configured type labels are escaped');
  await page.locator('#sensor-types').getByRole('checkbox', { name: 'VIS', exact: true }).focus();
  await page.keyboard.press('Space');
  await page.locator('#sensor-types').getByRole('checkbox', { name: 'PAN', exact: true }).check();
  assert.match(await page.locator('#sensor-types-help').textContent(), /2 selected/);
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
  assert.equal(preview.source.filters.gridTypes, undefined);
  assert.equal(preview.source.filters.sortieIds, undefined);
  assert.equal(preview.source.filters.isRealtime, false);
  assert.deepEqual(preview.source.filters.sensorNames, ['SENSOR_A']);
  assert.deepEqual(preview.source.filters.sensorTypes, ['VIS', 'PAN']);
  assert.match(await page.locator('#run-items').textContent(), /SENSOR_A · VIS/);
  assert.equal(preview.source.filters.createdTime.from, '2026-10-01T06:30:00.000Z');
  assert.equal(preview.source.filters.photoTime.to, '2026-10-20T09:00:00.000Z');
  assert.deepEqual(preview.source.filters.resolution, { min: 0.3, max: 0.3 });
  assert.equal(preview.targets[0].minOverlays, 25);
  assert.equal(preview.targets[1].ruleId, 'rule-2');
  assert.equal(await page.locator('#count-selected').textContent(), '25');
  assert.equal(await page.locator('#run-target-heading').textContent(), 'Selection by rule');
  assert.match(await page.locator('#run-target-description').textContent(), /not downstream dispatch counts/);
  assert.ok(await page.locator('#direct-run-note').isVisible(), 'gateway rule processing applies to selected-rule runs too');
  assert.match(await page.locator('#direct-run-note').textContent(), /Every mode sends ordinary overlay messages/);
  assert.match(await page.locator('#direct-run-note').textContent(), /current rules, including photo-age limits/);
  assert.match(await page.locator('#direct-run-note').textContent(), /other matching pipelines/);
  assert.match(await page.locator('.frozen-rule').first().textContent(), /Maximum photo age: 7 days/);
  assert.doesNotMatch(await page.locator('.frozen-rule').first().textContent(), /ignored/);
  assert.equal(await page.locator('#items-target-heading').textContent(), 'Selection rules');
  assert.equal(await page.locator('#refresh-run').count(), 0, 'redundant circular refresh action removed');
  assert.match(await page.locator('#run-memory').textContent(), /Process memory: 1 GiB/);
  const frozenSensorTypes = page.locator('.frozen-rule').nth(1);
  await frozenSensorTypes.locator('summary').click();
  assert.equal(await frozenSensorTypes.locator('.cell-sub').first().textContent(), `Sensor types: EO, ${sensorTypeLabel}`,
    'frozen rule details preserve sensor type conditions');
  assert.equal(await frozenSensorTypes.locator('img').count(), 0, 'sensor type labels are escaped in frozen details');
  assert.equal(await page.evaluate(() => window.sensorTypeInjected), undefined);
  await frozenSensorTypes.locator('summary').click();
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
  assert.deepEqual(await page.locator('#sensor-types input:checked').evaluateAll(inputs => inputs.map(input => input.value)), ['VIS', 'PAN'], 'sensor type draft survives run navigation');
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
  assert.equal(await directPage.locator('#run-target-heading').textContent(), 'Selection by rule');
  assert.ok(await directPage.locator('#run-target-table').isVisible(), 'selected-rule IDs retain local selection details');
  assert.ok(await directPage.locator('#direct-run-note').isVisible(), 'selected-rule IDs use ordinary gateway processing');
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
  assert.equal(await filtersPage.locator('#grid-types, #sortie-ids').count(), 0);
  assert.ok(await filtersPage.locator('#rule-selection').isHidden());
  assert.ok(await filtersPage.getByLabel('Maximum for Urban detail').isDisabled());
  assert.equal(await filtersPage.locator('#overall-max').getAttribute('placeholder'), 'Default: 100 overlays');
  assert.ok(await filtersPage.locator('#preview-form').evaluate(form => form.checkValidity()), 'hidden invalid rule quota cannot block filters');
  await filtersPage.locator('#sensor-names').fill('ar');
  await filtersPage.locator('#sensor-types').getByRole('checkbox', { name: 'Thermal imagery', exact: true }).check();
  await filtersPage.locator('#sensor-types').getByRole('checkbox', { name: 'PAN', exact: true }).check();
  await filtersPage.locator('[name="source-mode"][value="Rules"]').check();
  assert.equal(await filtersPage.locator('#sensor-types input:checked').count(), 2, 'Rules and Filters share the same sensor type draft');
  await filtersPage.locator('[name="source-mode"][value="Filters"]').check();
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
    source: { mode: 'Filters', filters: { sensorNames: ['ar'], sensorTypes: ['PAN', 'Thermal imagery'], resolution: { min: 1, max: 20 },
      photoTime: { from: '2026-10-04T16:40:00.000Z', to: '2026-10-05T16:40:00.000Z' } } },
    targets: [], minOverlays: 2, maxOverlays: 25, publication: { batchSize: 6, delayMs: 250 }
  });
  assert.ok(await filtersPage.locator('#run-target-table').isHidden());
  assert.ok(await filtersPage.locator('#direct-run-note').isVisible());
  assert.match(await filtersPage.locator('#provider-queries').textContent(), /logical_operators/);
  const frozenTypeQuery = JSON.parse(await filtersPage.locator('#provider-queries').textContent())[0].filter.logical_operators.AND.at(-1);
  assert.deepEqual(frozenTypeQuery, { match: { 'sensorType.keyword': { type: 'IN', values: ['PAN', 'IR'] } } }, 'frozen query displays exact server query values without translating them');
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
    assert.ok(await rerunPage.locator('#direct-run-note').isVisible(), 'all reruns use ordinary gateway processing');
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

  // A bounded partial preview remains usable after a memory or provider-page limit.
  const partialPage = await context.newPage();
  partialPage.on('pageerror', error => pageErrors.push(error.message));
  for (const stoppedReason of ['memoryLimit', 'payloadBudget', 'responseLimit', 'providerUnavailable']) {
    const partialRun = makeRun({ source: { mode: 'Filters' }, targets: [], minOverlays: 30,
      maxOverlays: 50, publication: { batchSize: 10, delayMs: 1000 } });
    partialRun.state = 'Ready';
    partialRun.sourceExhausted = false;
    partialRun.stoppedReason = stoppedReason;
    partialRun.shortfall = 5;
    partialRun.memory = { ...options.memory, processBytes: 10307921510, headroomBytes: 0, isUnderPressure: true };
    const startsBefore = requests.filter(request => request.path.endsWith('/start')).length;
    await partialPage.goto(`${origin}/ui/auto-run/runs/${partialRun.id}`);
    await partialPage.locator('#partial-selection-note').waitFor();
    assert.match(await partialPage.locator('#partial-selection-note').textContent(), /25 selected overlays were retained/);
    assert.match(await partialPage.locator('#run-notices').textContent(), /not a complete source total/);
    assert.match(await partialPage.locator('#run-memory').textContent(), /Headroom: 0 B/);
    assert.ok(await partialPage.locator('#start-run').isEnabled(), 'partial selection permits explicit publication');
    assert.equal(requests.filter(request => request.path.endsWith('/start')).length, startsBefore, 'memory limits never start a run automatically');
    if (stoppedReason === 'memoryLimit') {
      await partialPage.setViewportSize({ width: 390, height: 844 });
      await partialPage.screenshot({ path: path.join(screenshots, 'partial-memory-preview-mobile.png'), fullPage: true });
      assert.ok(await partialPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    }
    await partialPage.locator('#start-run').click();
    await partialPage.locator('#run-state').getByText('Publishing', { exact: true }).waitFor();
    assert.equal(requests.filter(request => request.path === `/auto-runs/${partialRun.id}/start`).length, 1);
  }
  await partialPage.close();

  // Provider progress and bounded diagnostics remain understandable while polling.
  const diagnosticsPage = await context.newPage();
  diagnosticsPage.on('pageerror', error => pageErrors.push(error.message));
  const diagnosticRun = makeRun({ source: { mode: 'Filters' }, targets: [], minOverlays: 0,
    maxOverlays: 50, publication: { batchSize: 10, delayMs: 1000 } });
  diagnosticRun.providerProgress = { phase: 'requesting', operation: 'continuation page', attempt: 1,
    maxAttempts: 3, queryIndex: 1, queryCount: 2, page: 4 };
  await diagnosticsPage.goto(`${origin}/ui/auto-run/runs/${diagnosticRun.id}`);
  await diagnosticsPage.locator('#provider-progress').filter({ hasText: 'Waiting for Elastic Provider to respond' }).waitFor();
  assert.match(await diagnosticsPage.locator('#provider-progress').textContent(), /query 1 of 2 · page 4 · attempt 1 of 3/);
  assert.equal(await diagnosticsPage.locator('#provider-progress').getAttribute('aria-live'), 'polite');
  diagnosticRun.providerProgress = { ...diagnosticRun.providerProgress, phase: 'retryWait', attempt: 2,
    lastStatusCode: 503, nextAttemptAt: new Date(Date.now() + 15000).toISOString() };
  await diagnosticsPage.locator('#provider-progress').filter({ hasText: 'Waiting to retry' }).waitFor();
  assert.match(await diagnosticsPage.locator('#provider-progress').textContent(), /in \d+\.\d s.*attempt 2 of 3.*HTTP 503/);
  diagnosticRun.providerProgress.phase = 'requestDelay';
  await diagnosticsPage.locator('#provider-progress').filter({ hasText: 'Waiting between provider requests' }).waitFor();
  const diagnosticMessage = 'Provider search stopped after 3 attempts. Collected overlays remain available.';
  diagnosticRun.state = 'Ready';
  diagnosticRun.providerProgress = null;
  diagnosticRun.sourceExhausted = false;
  diagnosticRun.stoppedReason = 'providerUnavailable';
  diagnosticRun.warnings = [diagnosticMessage, 'A legacy note remains visible.'];
  diagnosticRun.diagnosticsOmitted = 3;
  diagnosticRun.diagnostics = [{ timestamp: new Date().toISOString(), severity: 'Warning',
    code: 'provider_retries_exhausted', stage: 'Preparation', message: diagnosticMessage,
    traceId: 'trace-ui-123', queryIndex: 1, page: 4, upstream: { operation: 'continuation page', method: 'POST',
      statusCode: 503, attempts: 3, elapsedMs: 1250, contentType: 'application/json', bytesRead: 85,
      bodyExcerpt: '{"error":"<img src=x onerror=window.diagnosticInjected=true>","detail":"Search expired"}', bodyTruncated: false } },
    { timestamp: new Date().toISOString(), severity: 'Warning', code: 'provider_body_sample', stage: 'Preparation',
      message: 'A plain-text response sample.', upstream: { operation: 'initial search page', method: 'POST',
        statusCode: 502, attempts: 1, elapsedMs: 20, contentType: 'text/plain', bytesRead: 8193,
        bodyExcerpt: '<script>window.diagnosticInjected=true</script>\n' + 'proxy detail '.repeat(900), bodyTruncated: true } }];
  await diagnosticsPage.locator('#partial-selection-note').waitFor();
  assert.equal(await diagnosticsPage.getByText(diagnosticMessage, { exact: true }).count(), 1, 'structured messages are not repeated in legacy warnings');
  assert.ok(await diagnosticsPage.locator('#provider-progress').isHidden());
  assert.ok(await diagnosticsPage.locator('#start-run').isEnabled());
  assert.match(await diagnosticsPage.locator('.diagnostics-omitted').textContent(), /3 additional diagnostic entries/);
  const firstDiagnostic = diagnosticsPage.locator('#run-notices .diagnostic-details').first();
  await firstDiagnostic.locator('summary').focus();
  await diagnosticsPage.keyboard.press('Enter');
  assert.match(await firstDiagnostic.locator('.diagnostic-body').textContent(), /\n  "error":/);
  assert.match(await firstDiagnostic.textContent(), /trace-ui-123/);
  assert.match(await firstDiagnostic.textContent(), /HTTP status503/);
  const plainDiagnostic = diagnosticsPage.locator('#run-notices .diagnostic-details').nth(1);
  await plainDiagnostic.locator('summary').click();
  assert.ok((await plainDiagnostic.locator('.diagnostic-body').textContent()).length <= 8192);
  assert.match(await plainDiagnostic.locator('.diagnostic-truncated').textContent(), /shortened/);
  const diagnosticReads = reads.get(diagnosticRun.id);
  await diagnosticsPage.waitForFunction(() => document.getElementById('run-notices').querySelectorAll('details[open]').length === 2);
  while (reads.get(diagnosticRun.id) === diagnosticReads) await diagnosticsPage.waitForTimeout(100);
  assert.equal(await diagnosticsPage.locator('#run-notices details[open]').count(), 2, 'expanded diagnostics survive a poll');
  assert.equal(await diagnosticsPage.locator('#run-notices img, #run-notices script').count(), 0);
  assert.equal(await diagnosticsPage.evaluate(() => window.diagnosticInjected), undefined);
  await diagnosticsPage.setViewportSize({ width: 390, height: 844 });
  await diagnosticsPage.screenshot({ path: path.join(screenshots, 'provider-diagnostics-mobile.png'), fullPage: true });
  assert.ok(await diagnosticsPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth), 'long diagnostic excerpts fit mobile');
  diagnosticRun.state = 'Completed';
  diagnosticRun.counters.confirmed = diagnosticRun.counters.selected;
  await diagnosticsPage.locator('#run-outcome-note').filter({ hasText: 'Publication complete: 25 submissions' }).waitFor();
  assert.match(await diagnosticsPage.locator('#run-outcome-note').textContent(), /queued to the gateway; downstream processing is not tracked/);

  // Every action error can retain field, request and trace context; non-JSON bodies are escaped and bounded.
  await diagnosticsPage.goto(`${origin}/ui/auto-run`);
  await diagnosticsPage.locator('#prepare-button:not(:disabled)').waitFor();
  await diagnosticsPage.locator('[name="source-mode"][value="Filters"]').check();
  previewFailure = { status: 400, body: { error: 'Check the selected sensor types.', code: 'invalid_source',
    traceId: 'validation-trace', errors: { 'source.filters.sensorTypes': ['Choose a configured sensor type.'] } } };
  await diagnosticsPage.locator('#prepare-button').click();
  await diagnosticsPage.locator('#form-error .error-fields').waitFor();
  assert.match(await diagnosticsPage.locator('#form-error').textContent(), /source.filters.sensorTypes/);
  assert.match(await diagnosticsPage.locator('#form-error').textContent(), /POST \/auto-runs\/preview/);
  assert.match(await diagnosticsPage.locator('#form-error').textContent(), /validation-trace/);
  previewFailure = { status: 503, body: { error: diagnosticMessage, code: 'provider_unavailable',
    traceId: 'request-trace', diagnostic: diagnosticRun.diagnostics[0] } };
  await diagnosticsPage.locator('#prepare-button').click();
  await diagnosticsPage.locator('#form-error .diagnostic-details summary').click();
  assert.equal(await diagnosticsPage.locator('#form-error').getByText(diagnosticMessage, { exact: true }).count(), 1);
  assert.match(await diagnosticsPage.locator('#form-error .diagnostic-body').textContent(), /Search expired/);
  previewFailure = { status: 502, text: '<script>window.rawErrorInjected=true</script>\nProxy unavailable\n' + 'x'.repeat(70000) };
  await diagnosticsPage.locator('#prepare-button').click();
  await diagnosticsPage.locator('#form-error .diagnostic-details summary').click();
  const rawErrorText = await diagnosticsPage.locator('#form-error .diagnostic-body').textContent();
  assert.match(rawErrorText, /Proxy unavailable/);
  assert.ok(rawErrorText.length <= 8192);
  assert.match(await diagnosticsPage.locator('#form-error .diagnostic-truncated').textContent(), /shortened/);
  assert.equal(await diagnosticsPage.locator('#form-error script').count(), 0);
  assert.equal(await diagnosticsPage.evaluate(() => window.rawErrorInjected), undefined);
  previewFailure = null;
  await diagnosticsPage.close();

  // Empty Ready is a finished search, not a publishable selection, even after a partial scan.
  const emptyPreviewPage = await context.newPage();
  emptyPreviewPage.on('pageerror', error => pageErrors.push(error.message));
  for (const stoppedReason of [null, 'memoryLimit']) {
    const emptyRun = makeRun({ source: { mode: 'Filters' }, targets: [], minOverlays: 10,
      maxOverlays: 50, publication: { batchSize: 10, delayMs: 1000 } });
    emptyRun.state = 'Ready';
    emptyRun.sourceExhausted = stoppedReason === null;
    emptyRun.stoppedReason = stoppedReason;
    emptyRun.shortfall = 10;
    emptyRun.counters.selected = 0;
    emptyRun.counters.remaining = 0;
    emptyRun.counters.observedMatches = 0;
    await emptyPreviewPage.goto(`${origin}/ui/auto-run/runs/${emptyRun.id}`);
    await emptyPreviewPage.locator('#run-start-note').waitFor();
    assert.equal(await emptyPreviewPage.locator('#run-state').textContent(), 'Ready', 'backend state is preserved');
    assert.equal(await emptyPreviewPage.locator('#progress-label').textContent(), 'Preparation finished · no overlays selected');
    assert.ok(await emptyPreviewPage.locator('#start-run').isDisabled());
    assert.equal(await emptyPreviewPage.locator('#start-run').getAttribute('aria-describedby'), 'run-start-note');
    assert.match(await emptyPreviewPage.locator('#run-start-note').textContent(), /no selected overlays.*prepare a new preview/);
    assert.match(await emptyPreviewPage.locator('#run-notices').textContent(), /No selected overlays are available to run/);
    assert.ok(!(await emptyPreviewPage.locator('#run-notices').textContent()).includes('selection can still run'));
    assert.equal(await emptyPreviewPage.locator('#partial-selection-note').count(), 0);
    await emptyPreviewPage.evaluate(() => document.getElementById('start-run').dispatchEvent(new MouseEvent('click', { bubbles: true })));
    assert.equal(requests.filter(request => request.path === `/auto-runs/${emptyRun.id}/start`).length, 0, 'empty selections never issue start requests');
  }
  await emptyPreviewPage.setViewportSize({ width: 390, height: 844 });
  await emptyPreviewPage.screenshot({ path: path.join(screenshots, 'empty-ready-preview-mobile.png'), fullPage: true });
  assert.ok(await emptyPreviewPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
  await emptyPreviewPage.close();

  // Polling must not re-enable Start while a start request is pending; errors allow a deliberate retry.
  const startPage = await context.newPage();
  startPage.on('pageerror', error => pageErrors.push(error.message));
  const startRun = makeRun({ source: { mode: 'Filters' }, targets: [], minOverlays: 0,
    maxOverlays: 50, publication: { batchSize: 10, delayMs: 1000 } });
  startRun.state = 'Ready';
  await startPage.goto(`${origin}/ui/auto-run/runs/${startRun.id}`);
  await startPage.locator('#start-run:not([hidden])').waitFor();
  assert.ok(await startPage.locator('#start-run').isEnabled(), 'a nonempty Ready selection can start');
  assert.ok(await startPage.locator('#run-start-note').isHidden());
  let releaseStart;
  startGate = new Promise(resolve => { releaseStart = resolve; });
  pendingResponses.add(releaseStart);
  startFailure = true;
  const readsBeforeStart = reads.get(startRun.id);
  await startPage.locator('#start-run').click();
  await startPage.locator('#run-start-note').filter({ hasText: 'Waiting for Rules API' }).waitFor();
  assert.equal(await startPage.locator('#start-run').textContent(), 'Starting…');
  await startPage.waitForTimeout(5300);
  assert.ok(reads.get(startRun.id) > readsBeforeStart, 'run was polled while the start request was pending');
  assert.ok(await startPage.locator('#start-run').isDisabled(), 'polling preserves the pending start state');
  assert.ok(await startPage.locator('#rerun-run').isDisabled(), 'pending start cannot create a conflicting rerun');
  await startPage.evaluate(() => document.getElementById('start-run').dispatchEvent(new MouseEvent('click', { bubbles: true })));
  assert.equal(requests.filter(request => request.path === `/auto-runs/${startRun.id}/start`).length, 1);
  releaseStart();
  pendingResponses.delete(releaseStart);
  startGate = null;
  await startPage.locator('#run-error').filter({ hasText: 'Another operation is active' }).waitFor();
  await startPage.locator('#start-run:not(:disabled)').waitFor();
  assert.ok(await startPage.locator('#run-start-note').isHidden());
  assert.equal(await startPage.locator('#start-run').textContent(), 'Start run ↗');
  startFailure = false;
  await startPage.locator('#start-run').focus();
  await startPage.keyboard.press('Enter');
  await startPage.locator('#run-state').getByText('Publishing', { exact: true }).waitFor();
  assert.equal(requests.filter(request => request.path === `/auto-runs/${startRun.id}/start`).length, 2);
  await startPage.close();

  const searchPage = await context.newPage();
  searchPage.on('pageerror', error => pageErrors.push(error.message));
  await searchPage.goto(`${origin}/ui/auto-run`);
  await searchPage.locator('#prepare-button:not(:disabled)').waitFor();
  await searchPage.locator('#pipeline-select').selectOption('imagery');
  await searchPage.locator('[name="rule-choice"]').first().waitFor();
  await searchPage.locator('#rule-search').fill('uRBAn');
  await searchPage.waitForFunction(() => document.querySelectorAll('[name="rule-choice"]').length === 1);
  await searchPage.locator('[name="rule-choice"]').check();
  await searchPage.locator('#add-rules').click();
  await searchPage.getByLabel('Maximum for Urban detail').fill('17');
  await searchPage.locator('#rule-search').fill('rule-11');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Rule 11' }).waitFor();
  assert.equal(await searchPage.locator('[name="rule-choice"]').count(), 1, 'search includes rules beyond first page');
  await searchPage.locator('[name="rule-choice"]').check();
  await searchPage.locator('#add-rules').click();
  await searchPage.locator('#rule-search').fill('EO');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Archive coverage' }).waitFor();
  await searchPage.locator('#rule-status').selectOption('true');
  await searchPage.locator('#rule-page-label').filter({ hasText: 'No rules' }).waitFor();
  await searchPage.locator('#rule-status').selectOption('false');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Archive coverage' }).waitFor();
  assert.equal(await searchPage.locator('#select-all-rules').textContent(), 'Select all matching rules');
  await searchPage.locator('#select-all-rules').click();
  await searchPage.locator('#rule-selection-status').filter({ hasText: 'Added 1 rule' }).waitFor();
  assert.equal(await searchPage.locator('.target-table tbody tr').count(), 3);
  await searchPage.locator('#rule-status').selectOption('');
  await searchPage.locator('#rule-search').fill('sensor_a');
  await searchPage.waitForFunction(() => document.querySelectorAll('[name="rule-choice"]').length === 10);
  await searchPage.locator('#select-all-rules').click();
  await searchPage.locator('#rule-selection-status').filter({ hasText: 'Added 8 rules' }).waitFor();
  assert.equal(await searchPage.locator('.target-table tbody tr').count(), 11);
  assert.equal(await searchPage.getByLabel('Maximum for Urban detail').inputValue(), '17');
  assert.ok(requests.some(request => request.path === '/pipelines/imagery/rules' && new URLSearchParams(request.search).get('search') === 'sensor_a' && new URLSearchParams(request.search).get('from') === '10'));
  rules[9].description = 'Coastal sample';
  await searchPage.locator('#rule-search').fill('coastal');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Rule 10' }).waitFor();
  assert.equal(await searchPage.locator('[name="rule-choice"]').count(), 1);
  let releaseStale, enteredStale;
  const staleEntered = new Promise(resolve => { enteredStale = resolve; });
  const staleGate = new Promise(resolve => { releaseStale = resolve; });
  pendingResponses.add(releaseStale);
  rulePageOverrides.set('imagery', async (from, size, parameters) => {
    if (parameters.get('search') === 'stale') { enteredStale(); await staleGate; return { data: [rules[10]] }; }
    return { data: [rules[0]] };
  });
  await searchPage.locator('#rule-search').fill('stale');
  await staleEntered;
  await searchPage.locator('#rule-search').fill('urban');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Urban detail' }).waitFor();
  releaseStale(); pendingResponses.delete(releaseStale); rulePageOverrides.delete('imagery');
  await searchPage.waitForTimeout(100);
  assert.equal(await searchPage.locator('#rule-list .rule-choice-name').textContent(), 'Urban detail · added', 'cancelled search cannot overwrite a newer result');
  await searchPage.getByRole('link', { name: 'Rules', exact: true }).click();
  assert.equal(await searchPage.title(), 'Rules · Flarules');
  assert.equal(await searchPage.locator('#coming-soon-page p').textContent(), 'Coming Soon');
  assert.equal(await searchPage.locator('.nav-item[aria-current="page"]').textContent(), '≡Rules');
  await searchPage.getByRole('link', { name: 'Rules Simulate', exact: true }).click();
  assert.equal(await searchPage.title(), 'Rules Simulate · Flarules');
  await searchPage.reload();
  await searchPage.getByRole('heading', { name: 'Rules Simulate', exact: true }).waitFor();
  await searchPage.getByRole('link', { name: 'Auto Run 01', exact: true }).click();
  await searchPage.locator('#prepare-button:not(:disabled)').waitFor();
  await searchPage.locator('#pipeline-select').selectOption('imagery');
  await searchPage.locator('#rule-search').fill('urban');
  await searchPage.locator('.rule-choice-name').filter({ hasText: 'Urban detail' }).waitFor();
  await searchPage.locator('[name="rule-choice"]').check();
  await searchPage.locator('#add-rules').click();
  await searchPage.getByLabel('Maximum for Urban detail').fill('23');
  await searchPage.getByRole('link', { name: 'Rules', exact: true }).click();
  await searchPage.getByRole('link', { name: 'Auto Run 01', exact: true }).click();
  assert.equal(await searchPage.getByLabel('Maximum for Urban detail').inputValue(), '23', 'navigation keeps the Auto Run draft');
  assert.equal(await searchPage.locator('#rule-search').inputValue(), 'urban');
  await searchPage.screenshot({ path: path.join(screenshots, 'rule-search-and-history.png'), fullPage: true });
  await searchPage.setViewportSize({ width: 390, height: 844 });
  assert.ok(await searchPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await searchPage.getByRole('link', { name: 'Rules Simulate', exact: true }).focus();
  await searchPage.keyboard.press('Enter');
  assert.equal(await searchPage.locator('#coming-soon-page p').textContent(), 'Coming Soon');
  assert.ok(await searchPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await searchPage.screenshot({ path: path.join(screenshots, 'coming-soon-mobile.png'), fullPage: true });
  await searchPage.close();

  // Retained history exposes every job, honest sizes, recoverable failures, and asynchronous cleanup.
  const cleanupPage = await context.newPage();
  cleanupPage.on('pageerror', error => pageErrors.push(error.message));
  for (const run of runs.values()) run.state = 'Completed';
  const cleanupBody = { source: { mode: 'OverlayIds', overlayIds: ['cleanup-overlay'] }, targets: [], minOverlays: 0, publication: { batchSize: 1, delayMs: 0 } };
  const immediateRun = makeRun(cleanupBody); immediateRun.state = 'Ready'; immediateRun.retainedBytes = 512;
  const pendingRun = makeRun(cleanupBody); pendingRun.state = 'Publishing'; pendingRun.retainedBytes = 1536;
  await cleanupPage.goto(`${origin}/ui/auto-run`);
  await cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`).waitFor();
  assert.ok(runs.size > 8);
  assert.equal(await cleanupPage.locator('.history-item').count(), runs.size, 'history must show all runs targeted by Delete all');
  assert.match(await cleanupPage.locator('.history-help').textContent(), /not total RAM/);
  assert.equal(await cleanupPage.locator('.history-item').filter({ has: cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`) }).locator('.history-storage span').textContent(), '512 B retained');
  assert.equal(await cleanupPage.locator('.history-item').filter({ has: cleanupPage.locator(`[data-delete-run="${pendingRun.id}"]`) }).locator('.history-storage span').textContent(), '1.5 KiB retained');
  assert.equal(await cleanupPage.locator('.history-item a button').count(), 0);
  deletionFailure = true;
  await cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`).click();
  await cleanupPage.locator('#history-error').filter({ hasText: 'Cleanup unavailable' }).waitFor();
  assert.ok(await cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`).isEnabled());
  deletionFailure = false;
  await cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`).focus();
  await cleanupPage.keyboard.press('Enter');
  await cleanupPage.locator(`[data-delete-run="${immediateRun.id}"]`).waitFor({ state: 'detached' });
  assert.ok(!runs.has(immediateRun.id));
  await cleanupPage.locator(`[data-delete-run="${pendingRun.id}"]`).click();
  await cleanupPage.locator(`[data-delete-run="${pendingRun.id}"]:disabled`).waitFor();
  await cleanupPage.locator('#history-status').filter({ hasText: 'awaiting in-flight work' }).waitFor();
  await cleanupPage.locator(`[data-delete-run="${pendingRun.id}"]`).waitFor({ state: 'detached' });
  assert.match(await cleanupPage.locator('#history-status').textContent(), /Pending deletions finished/);
  const pendingBulk = makeRun(cleanupBody); pendingBulk.state = 'Publishing';
  await cleanupPage.locator('#refresh-history').click();
  await cleanupPage.locator(`[data-delete-run="${pendingBulk.id}"]`).waitFor();
  await cleanupPage.setViewportSize({ width: 390, height: 844 });
  assert.ok(await cleanupPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await cleanupPage.screenshot({ path: path.join(screenshots, 'run-cleanup-mobile.png'), fullPage: true });
  await cleanupPage.locator('#delete-all-runs').click();
  await cleanupPage.locator(`[data-delete-run="${pendingBulk.id}"]:disabled`).waitFor();
  await cleanupPage.locator('#history-list').filter({ hasText: 'No retained runs yet' }).waitFor();
  assert.equal(runs.size, 0);
  assert.match(await cleanupPage.locator('#history-summary').textContent(), /0 retained runs · 0 B/);
  assert.ok(await cleanupPage.locator('#delete-all-runs').isDisabled());
  const finalListRequests = requests.filter(request => request.path === '/auto-runs' && request.method === 'GET').length;
  await cleanupPage.waitForTimeout(2200);
  assert.equal(requests.filter(request => request.path === '/auto-runs' && request.method === 'GET').length, finalListRequests, 'cleanup polling stops after pending jobs disappear');
  await cleanupPage.goto(`${origin}/ui/auto-run/runs/${immediateRun.id}`);
  await cleanupPage.locator('#run-error').filter({ hasText: 'deleted' }).waitFor();
  assert.ok(await cleanupPage.locator('#start-run').isHidden());
  await cleanupPage.close();

  options.ui.mapUrl = null;
  const noMapPage = await context.newPage();
  noMapPage.on('pageerror', error => pageErrors.push(error.message));
  await noMapPage.goto(`${origin}/ui/auto-run`);
  await noMapPage.locator('#prepare-button:not(:disabled)').waitFor();
  await noMapPage.locator('[name="source-mode"][value="Filters"]').check();
  assert.ok(await noMapPage.locator('#open-area-map').isDisabled());
  assert.match(await noMapPage.locator('#area-map-hint').textContent(), /AutoRun.Ui.MapUrl/);
  assert.equal(await noMapPage.locator('#area-map-frame').getAttribute('src'), null);
  await noMapPage.close();
  options.ui.mapUrl = `${origin}/map-test`;

  // Clearing filters preserves the rule/ID/publication draft; explicit minimum zero is submitted in both modes.
  const filterControlsPage = await context.newPage();
  filterControlsPage.on('pageerror', error => pageErrors.push(error.message));
  await filterControlsPage.goto(`${origin}/ui/auto-run`);
  await filterControlsPage.locator('#prepare-button:not(:disabled)').waitFor();
  await filterControlsPage.locator('#pipeline-select').selectOption('imagery');
  await filterControlsPage.locator('[name="rule-choice"]').first().check();
  await filterControlsPage.locator('#add-rules').click();
  await filterControlsPage.locator('#rule-search').fill('Urban');
  await filterControlsPage.locator('#batch-size').fill('7');
  await filterControlsPage.locator('#delay-ms').fill('150');
  await filterControlsPage.locator('#overall-max').fill('30');
  await filterControlsPage.locator('[name="source-mode"][value="OverlayIds"]').check();
  await filterControlsPage.locator('#overlay-ids').fill('keep-this-id');
  for (const mode of ['Rules', 'Filters']) {
    await filterControlsPage.locator(`[name="source-mode"][value="${mode}"]`).check();
    await filterControlsPage.locator('#filter-details').evaluate(element => { element.open = true; });
    for (const id of ['sensor-names', 'area-wkt']) await filterControlsPage.locator(`#${id}`).fill('invalid-search');
    for (const id of ['created-from', 'created-to', 'photo-from', 'photo-to']) await filterControlsPage.locator(`#${id}`).fill('2026-10-05T12:00');
    await filterControlsPage.locator('#registration-quality').selectOption('Accurate');
    await filterControlsPage.locator('#is-realtime').selectOption('true');
    await filterControlsPage.locator('#sensor-types').getByRole('checkbox', { name: 'VIS', exact: true }).check();
    await filterControlsPage.locator('#resolution-min').fill('0');
    await filterControlsPage.locator('#resolution-max').fill('20');
    await filterControlsPage.locator('#prepare-button').click();
    await filterControlsPage.locator('#form-error').filter({ hasText: 'WKT' }).waitFor();
    const beforeClear = requests.filter(request => request.path === '/auto-runs/preview').length;
    await filterControlsPage.locator('#clear-filters').focus();
    await filterControlsPage.keyboard.press('Enter');
    assert.ok(await filterControlsPage.locator('#form-error').isHidden());
    assert.equal(await filterControlsPage.locator('#sensor-types input:checked').count(), 0);
    for (const id of ['sensor-names', 'registration-quality', 'is-realtime', 'resolution-min', 'resolution-max', 'area-wkt', 'created-from', 'created-to', 'photo-from', 'photo-to'])
      assert.equal(await filterControlsPage.locator(`#${id}`).inputValue(), '', `${mode}: ${id} cleared`);
    assert.equal(await filterControlsPage.locator('#batch-size').inputValue(), '7');
    assert.equal(await filterControlsPage.locator('#delay-ms').inputValue(), '150');
    assert.equal(await filterControlsPage.locator('#overall-max').inputValue(), '30');
    assert.equal(await filterControlsPage.locator('#rule-search').inputValue(), 'Urban');
    assert.equal(await filterControlsPage.locator('#overlay-ids').inputValue(), 'keep-this-id');
    assert.equal(await filterControlsPage.locator('[data-target-field="maxOverlays"]').count(), 1);
    assert.equal(requests.filter(request => request.path === '/auto-runs/preview').length, beforeClear, 'clear does not submit or change a frozen preview');
    await filterControlsPage.locator('#resolution-min').fill('0');
    await filterControlsPage.locator('#resolution-max').fill('0');
    await filterControlsPage.locator('#prepare-button').click();
    await filterControlsPage.locator('#form-error').filter({ hasText: 'maximum resolution must be positive' }).waitFor();
    await filterControlsPage.locator('#resolution-max').fill('20');
    await filterControlsPage.locator('#prepare-button').click();
    await filterControlsPage.locator('#start-run:not([hidden])').waitFor();
    const sent = requests.filter(request => request.path === '/auto-runs/preview').at(-1).body;
    assert.deepEqual(sent.source.filters, { resolution: { min: 0, max: 20 } });
    assert.equal(sent.source.mode, mode);
    assert.equal(sent.targets.length, mode === 'Rules' ? 1 : 0);
    await filterControlsPage.locator('.back-link').click();
    await filterControlsPage.locator('#prepare-button:not(:disabled)').waitFor();
  }
  await filterControlsPage.close();

  // Lightweight history supports simultaneous jobs and keeps queue positions fresh without loading frozen rules.
  const queuedPreparation = makeRun(cleanupBody);
  queuedPreparation.state = 'QueuedPreparation'; queuedPreparation.queuePosition = 3; queuedPreparation.waitReason = 'Waiting for memory headroom';
  const queuedPublication = makeRun(cleanupBody); queuedPublication.state = 'Ready';
  const workingPublication = makeRun(cleanupBody); workingPublication.state = 'Publishing';
  const concurrentPage = await context.newPage();
  concurrentPage.on('pageerror', error => pageErrors.push(error.message));
  await concurrentPage.goto(`${origin}/ui/auto-run`);
  const queuedCard = concurrentPage.locator(`.history-item:has([data-delete-run="${queuedPreparation.id}"])`);
  await queuedCard.getByText('Queued for preparation', { exact: true }).waitFor();
  assert.match(await queuedCard.textContent(), /Queue position 3.*Waiting for memory headroom/);
  assert.match(await concurrentPage.locator('#server-limits').textContent(), /Preparing2 \/ 3/);
  assert.ok(requests.filter(request => request.path === '/auto-runs' && request.method === 'GET').every(request => request.search === '?summaryOnly=true'));
  queuedPreparation.queuePosition = 1;
  await queuedCard.locator('.history-queue').filter({ hasText: 'Queue position 1' }).waitFor();
  await concurrentPage.locator(`a[href="/ui/auto-run/runs/${queuedPublication.id}"]`).click();
  await concurrentPage.locator('#start-run:not([hidden])').waitFor();
  queueStart = true;
  await concurrentPage.locator('#start-run').click();
  await concurrentPage.locator('#run-state').getByText('Queued for publication', { exact: true }).waitFor();
  assert.ok(await concurrentPage.locator('#start-run').isHidden());
  assert.ok(await concurrentPage.locator('#rerun-run').isHidden());
  assert.ok(await concurrentPage.locator('#cancel-run').isEnabled());
  assert.ok(await concurrentPage.locator('#update-publication').isEnabled());
  assert.match(await concurrentPage.locator('#run-queue-note').textContent(), /Queue position 2.*will not expire/);
  await concurrentPage.reload();
  await concurrentPage.locator('#run-state').getByText('Queued for publication', { exact: true }).waitFor();
  assert.equal(requests.filter(request => request.path === `/auto-runs/${queuedPublication.id}/start`).length, 1);
  await concurrentPage.locator('#cancel-run').click();
  await concurrentPage.locator('#run-state').getByText('Cancelled', { exact: true }).waitFor();
  assert.ok(await concurrentPage.locator('#run-queue-note').isHidden());
  queueStart = false;
  await concurrentPage.goto(`${origin}/ui/auto-run/runs/${queuedPreparation.id}`);
  await concurrentPage.locator('#run-state').getByText('Queued for preparation', { exact: true }).waitFor();
  await concurrentPage.setViewportSize({ width: 390, height: 844 });
  assert.ok(await concurrentPage.evaluate(() => document.documentElement.scrollWidth <= innerWidth + 1));
  await concurrentPage.screenshot({ path: path.join(screenshots, 'queued-run-mobile.png'), fullPage: true });
  await concurrentPage.locator('#cancel-run').click();
  await concurrentPage.locator('#run-state').getByText('Cancelled', { exact: true }).waitFor();
  await concurrentPage.close();

  // Countdown is updated locally, corrects server/browser clock skew, and never invents a capacity ETA.
  const countdownPage = await context.newPage();
  countdownPage.on('pageerror', error => pageErrors.push(error.message));
  await countdownPage.addInitScript(() => {
    const intervals = new Set(), create = window.setInterval.bind(window), remove = window.clearInterval.bind(window);
    window.countdownIntervals = intervals;
    window.setInterval = (callback, delay, ...args) => { const id = create(callback, delay, ...args); if (delay === 100) intervals.add(id); return id; };
    window.clearInterval = id => { intervals.delete(id); remove(id); };
  });
  const countdownRun = makeRun(cleanupBody);
  countdownRun.providerProgress = { phase: 'requestDelay', operation: 'initial search page', attempt: 1, maxAttempts: 3 };
  let countdownDuration = 150;
  runResponseOverrides.set(countdownRun.id, () => {
    const serverTime = Date.now() + 3600000;
    return { serverTime: new Date(serverTime).toISOString(), providerProgress: { ...countdownRun.providerProgress,
      nextAttemptAt: countdownRun.providerProgress.phase === 'capacityWait' ? null : new Date(serverTime + countdownDuration).toISOString() } };
  });
  await countdownPage.goto(`${origin}/ui/auto-run/runs/${countdownRun.id}`);
  await countdownPage.locator('#provider-progress').filter({ hasText: /next eligible request in \d+ ms/ }).waitFor();
  assert.doesNotMatch(await countdownPage.locator('#provider-progress').textContent(), /in 1(?:\.0)? s/);
  const beforeCountdown = reads.get(countdownRun.id);
  await countdownPage.locator('#provider-progress').filter({ hasText: 'Delay elapsed' }).waitFor();
  assert.equal(reads.get(countdownRun.id), beforeCountdown, 'countdown ticks do not make extra API calls');
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 0, 'elapsed countdown stops ticking');
  countdownDuration = 2500;
  await countdownPage.locator('#provider-progress').filter({ hasText: /in 2\.\d s/ }).waitFor();
  const countdownText = await countdownPage.locator('#provider-progress').textContent();
  await countdownPage.waitForTimeout(250);
  assert.notEqual(await countdownPage.locator('#provider-progress').textContent(), countdownText, 'remaining time moves between polls');
  await countdownPage.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: true }); document.dispatchEvent(new Event('visibilitychange')); });
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 0, 'hidden page stops the timer');
  await countdownPage.evaluate(() => { Object.defineProperty(document, 'hidden', { configurable: true, value: false }); document.dispatchEvent(new Event('visibilitychange')); });
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 1);
  countdownRun.providerProgress.phase = 'capacityWait';
  await countdownPage.locator('#provider-progress').filter({ hasText: 'No start time is available' }).waitFor();
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 0);
  countdownRun.providerProgress.phase = 'retryWait'; countdownDuration = 6000;
  await countdownPage.locator('#provider-progress').filter({ hasText: 'Waiting for the shared Elastic Provider cooldown' }).waitFor();
  countdownRun.providerProgress.phase = 'retryWait'; countdownRun.providerProgress.attempt = 2; countdownRun.providerProgress.lastStatusCode = 503; countdownDuration = 9000;
  await countdownPage.locator('#provider-progress').filter({ hasText: /retry Elastic Provider in 9\.0 s/ }).waitFor();
  await countdownPage.locator('.back-link').click();
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 0, 'navigation stops countdown');
  await countdownPage.goto(`${origin}/ui/auto-run/runs/${countdownRun.id}`);
  await countdownPage.locator('#provider-progress').filter({ hasText: 'Waiting to retry' }).waitFor();
  countdownRun.state = 'Failed';
  await countdownPage.locator('#run-state').getByText('Failed', { exact: true }).waitFor();
  assert.equal(await countdownPage.evaluate(() => window.countdownIntervals.size), 0, 'terminal state stops countdown');
  await countdownPage.close();

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
    'rule and overlay-ID reruns work from retained deep links without configuration drafts',
    'grid-type and sortie filters absent from both search modes and submitted payloads',
    'configured map modal loads on demand, supports keyboard focus and Escape, and unloads on close',
    'map modal mobile layout and absent-configuration hint',
    'redundant run refresh removed while automatic polling remains',
    'memory, payload and response limits preserve explicitly startable partial previews with memory readouts',
    'sensor-type-only rules display escaped conditions in rule browsing and frozen details',
    'empty Ready previews explain disabled Start and cannot issue publication requests',
    'pending Start survives polling, rejects repeated clicks and recovers after a failed request',
    'server rule search by name ID sensor type and description with status, full paging, matching bulk selection and preserved quotas',
    'cancelled searches cannot overwrite newer results',
    'Rules and Rules Simulate Coming Soon routes, deep links, mobile keyboard navigation and preserved Auto Run drafts',
    'all retained runs show data weights with individual and bulk deletion, pending cleanup polling, errors and deleted deep links',
    'configured sensor type multi-select preserves shared drafts, local names and exact frozen provider values',
    'provider request pacing and retry progress, partial provider failure and queued-only success wording',
    'bounded escaped JSON and plain-text diagnostics preserve expansion across polling and fit mobile',
    'action errors retain validation fields, request status, trace identifiers and bounded non-JSON bodies',
    'zero minimum resolution and clear filters in both modes preserve rules IDs quotas and publication settings',
    'lightweight concurrent history, queued Start, refresh, cancellation and responsive queue details',
    'monotonic countdown handles milliseconds seconds server clock skew capacity waits and timer cleanup'], screenshots }, null, 2));
} finally {
  for (const release of pendingResponses) release();
  if (browser) await browser.close();
  await new Promise(resolve => server.close(resolve));
}
