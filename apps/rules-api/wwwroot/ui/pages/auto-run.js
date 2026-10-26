import { api } from '../api.js';
import { navigate } from '../router.js';

const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const number = value => Number(value || 0).toLocaleString();
const bytes = value => {
  const amount = Number(value);
  if (!Number.isFinite(amount) || amount < 0) return '—';
  const unit = amount >= 1024 ** 3 ? 3 : amount >= 1024 ** 2 ? 2 : amount >= 1024 ? 1 : 0;
  return `${(amount / 1024 ** unit).toLocaleString(undefined, { maximumFractionDigits: 1 })} ${['B', 'KiB', 'MiB', 'GiB'][unit]}`;
};
const splitValues = value => [...new Set(value.split(/[\n,]+/).map(v => v.trim()).filter(Boolean))];
const terminal = state => ['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Expired'].includes(state);
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
const state = {
  initialized: false, options: null, targets: [], rules: [], rulesFrom: 0, rulesSize: 10,
  pipelines: [], runId: null, run: null, timer: null, itemsFrom: 0, itemsSize: 20,
  itemsOutcome: '', publicationDirty: false, refreshVersion: 0, rulesVersion: 0,
  rulesLoading: false, rulesPipeline: '', rulesSearch: '', rulesStatus: '', rulesTimer: null, rulesController: null, resumeRules: false,
  selectionOperation: null, setupReady: false, preparing: false, rerunPending: false,
  history: [], historyVersion: 0, historyTimer: null, historyDeleting: false, historyPendingIds: new Set()
};
const $ = id => document.getElementById(id);
const pendingRunActions = new Map();
const directSource = () => document.querySelector('[name="source-mode"]:checked')?.value === 'Filters'
  || (document.querySelector('[name="source-mode"]:checked')?.value === 'OverlayIds'
    && document.querySelector('[name="id-processing"]:checked')?.value === 'direct');
const directRun = run => ['OverlayIds', 'Filters'].includes(run?.sourceMode) && run.targets?.length === 0;
const badge = value => `<span class="tag ${['Ready', 'Completed', 'Confirmed'].includes(value) ? 'good' : ['Failed', 'Expired'].includes(value) ? 'bad' : ['CompletedWithErrors', 'Uncertain', 'Cancelled'].includes(value) ? 'warn' : ''}">${escape(value)}</span>`;
const formatTime = value => value ? new Date(value).toLocaleString(undefined, { dateStyle: 'medium', timeStyle: 'short' }) : '—';
const field = (id, label, placeholder = '', type = 'text', extra = '') =>
  `<label class="field" for="${id}"><span>${label}</span><input id="${id}" name="${id}" type="${type}" placeholder="${placeholder}" ${extra}></label>`;
const panelHeader = (step, title, description) => `<div class="panel-header"><span class="step-number">${step}</span><div><h2>${title}</h2><p>${description}</p></div></div>`;
const publicationPresets = (prefix = '') => `<div id="${prefix}publication-presets" class="publication-presets" role="group" aria-label="Publication presets">
  ${[['Sequential', 1, 1000], ['Balanced', 10, 1000], ['Burst', 100, 0]].map(([name, batch, delay]) =>
    `<button type="button" class="button compact ghost publication-preset" data-batch="${batch}" data-delay="${delay}" aria-pressed="false" aria-label="${name}: ${batch} overlays per batch, ${delay} milliseconds between batches"><span>${name}</span><small>${batch} / ${delay ? '1 s' : 'no delay'}</small></button>`).join('')}
  </div><p class="preset-hint hint">${prefix ? 'Choose a preset or edit the values, then Apply settings.' : 'Choose a starting preset, then adjust either value below.'}</p>`;

function attachPublicationPresets(prefix = '') {
  $(`${prefix}publication-presets`).addEventListener('click', event => {
    const button = event.target.closest('[data-batch]');
    if (!button || button.disabled) return;
    $(`${prefix}batch-size`).value = button.dataset.batch;
    $(`${prefix}delay-ms`).value = button.dataset.delay;
    // Follow the same dirty-state path as manually editing either field.
    $(`${prefix}batch-size`).dispatchEvent(new Event('input', { bubbles: true }));
  });
  for (const name of ['batch-size', 'delay-ms'])
    $(`${prefix}${name}`).addEventListener('input', () => syncPublicationPresets(prefix));
  syncPublicationPresets(prefix);
}

function syncPublicationPresets(prefix = '', disabled = false) {
  for (const button of $(`${prefix}publication-presets`).querySelectorAll('[data-batch]')) {
    button.disabled = disabled;
    button.setAttribute('aria-pressed', String(
      button.dataset.batch === $(`${prefix}batch-size`).value && button.dataset.delay === $(`${prefix}delay-ms`).value));
  }
}

export async function showAutoRun(id = null) {
  cancelRuleSelection();
  if ($('area-map-dialog')?.open) $('area-map-dialog').close();
  if (!state.initialized || !$('configuration')) {
    state.initialized = true;
    state.options = null;
    state.setupReady = false;
    state.rulesSearch = '';
    state.rulesStatus = '';
    $('app').innerHTML = `<div id="global-notice" role="alert" hidden></div><div id="configuration">${configurationMarkup()}</div><div id="run-detail" hidden></div>`;
    attachConfiguration();
    loadSetup();
  }
  clearTimeout(state.timer);
  clearTimeout(state.historyTimer);
  state.refreshVersion++;
  state.runId = id;
  $('configuration').hidden = Boolean(id);
  $('run-detail').hidden = !id;
  if (!id) {
    document.title = 'Auto Run · Flarules';
    loadHistory();
    if (state.resumeRules && $('pipeline-select').value) loadRules();
    state.resumeRules = false;
    return;
  }
  state.run = null;
  state.itemsFrom = 0;
  state.itemsOutcome = '';
  state.publicationDirty = false;
  $('run-detail').innerHTML = detailMarkup(id);
  attachDetail();
  document.title = 'Run details · Flarules';
  await refreshRun(true);
}

export function stopAutoRun() {
  clearTimeout(state.timer);
  clearTimeout(state.historyTimer);
  clearTimeout(state.rulesTimer);
  state.refreshVersion++;
  state.historyVersion++;
  state.rulesVersion++;
  state.rulesController?.abort();
  state.resumeRules ||= state.rulesLoading;
  state.rulesLoading = false;
  state.runId = null;
  cancelRuleSelection();
  if ($('area-map-dialog')?.open) $('area-map-dialog').close();
}

function configurationMarkup() {
  return `<div class="page-heading"><div><h1>Give historical data a new run.</h1><p>Find overlays and review exactly what will be sent to the Unified Gateway.</p></div></div>
  <div class="configure-layout"><form id="preview-form">
    <section class="panel">${panelHeader('01', 'Choose your source', 'Start with saved rules, custom filters, or specific overlays.')}<div class="panel-body">
      <div class="source-modes">
        ${[['Rules', '⌘', 'From rules', 'Search using rule conditions'], ['Filters', '≋', 'From filters', 'Define your own search'], ['OverlayIds', '▦', 'Overlay IDs', 'Use a specific collection']].map(([value, icon, title, desc], i) =>
          `<label class="source-option"><input type="radio" name="source-mode" value="${value}" ${i === 0 ? 'checked' : ''}><div class="source-symbol" aria-hidden="true">${icon}</div><strong>${title}</strong><small>${desc}</small></label>`).join('')}
      </div>
      <p id="source-description" class="source-description">Search conditions are derived from the rules you select below. Additional filters can narrow the search.</p>
      <div id="overlay-ids-field" hidden><div class="divider"></div><label class="field" for="overlay-ids"><span>Overlay IDs</span><textarea id="overlay-ids" placeholder="overlay-001&#10;overlay-002" spellcheck="false"></textarea><small>Separate IDs with commas or new lines. Duplicate IDs are counted once.</small></label>
        <fieldset class="id-processing"><legend>Processing</legend>
          <label><input type="radio" name="id-processing" value="direct" checked><span><strong>Send directly to gateway</strong><small>The gateway selects pipelines and applies current rules, including photo-age limits.</small></span></label>
          <label><input type="radio" name="id-processing" value="rules"><span><strong>Match selected rules</strong><small>Preview matches against selected rules and freeze their definitions. Historical photo-age limits are ignored.</small></span></label>
        </fieldset>
      </div>
      <details id="filter-details" class="filter-details"><summary>Overlay filters <span id="filters-description" class="muted">Optional for rule-derived searches</span></summary><div>
        <div class="field-row">${field('sensor-names', 'Sensor names', 'e.g. SENSOR_A, SENSOR_B')}<fieldset id="sensor-types" class="sensor-type-selector" aria-describedby="sensor-types-help"><legend>Sensor types</legend><div id="sensor-type-options" class="sensor-type-options"></div><p id="sensor-types-help" class="hint">Loading sensor types…</p></fieldset></div>
        <div class="field-row"><label class="field" for="registration-quality"><span>Registration quality</span><select id="registration-quality"><option value="">Any quality</option><option value="Accurate">Accurate</option><option value="Sensor">Sensor</option><option value="Accurate,Sensor">Accurate or Sensor</option></select></label><label class="field" for="is-realtime"><span>Realtime overlay</span><select id="is-realtime"><option value="">Any</option><option value="true">Yes</option><option value="false">No</option></select></label></div>
        <div class="field-row">${field('resolution-min', 'Minimum resolution', 'No lower bound', 'number', 'min="0" step="any"')}${field('resolution-max', 'Maximum resolution', 'No upper bound', 'number', 'min="0" step="any"')}</div>
        <div class="field search-area-field"><div class="search-area-heading"><label class="field-label" for="area-wkt">Search area · WKT</label><button id="open-area-map" type="button" class="button compact ghost" aria-haspopup="dialog" aria-controls="area-map-dialog" aria-describedby="area-map-hint" disabled>Open map</button></div><textarea id="area-wkt" class="mono" placeholder="POLYGON ((...)) or MULTIPOLYGON (((...)))" spellcheck="false"></textarea><small>Selects intersecting overlays. Gateway rules determine the dispatched ROI.</small><small id="area-map-hint">Loading map settings…</small></div>
        <div class="filter-subheading">Time ranges</div>
        <p class="filter-note">Times use your browser’s local timezone (${escape(localZone)}) and are sent as UTC. Both boundaries are optional.</p>
        <div class="field-row">${field('created-from', 'Created from', '', 'datetime-local')}${field('created-to', 'Created to', '', 'datetime-local')}</div>
        <div class="field-row">${field('photo-from', 'Photo taken from', '', 'datetime-local')}${field('photo-to', 'Photo taken to', '', 'datetime-local')}</div>
      </div></details>
    </div></section>
    <fieldset id="rule-selection" class="panel rule-selection" aria-label="Selected pipeline rules">${panelHeader('02', 'Select pipeline rules', 'Each selected rule gets its own overlay quota.')}<div class="panel-body">
      <div class="toolbar"><label class="field" for="pipeline-select"><span>Pipeline</span><select id="pipeline-select"><option value="">Loading pipelines…</option></select></label><button type="button" class="button ghost" id="load-rules">Browse rules <span aria-hidden="true">↓</span></button></div>
      <div class="rule-search-controls"><label class="field" for="rule-search"><span>Search rules</span><input id="rule-search" type="search" maxlength="200" placeholder="Name, rule ID, sensor or sensor type" aria-describedby="rule-search-help" autocomplete="off"></label><label class="field" for="rule-status"><span>Status</span><select id="rule-status"><option value="">Active and inactive</option><option value="true">Active only</option><option value="false">Inactive only</option></select></label></div>
      <p id="rule-search-help" class="hint">Search every page in the selected pipeline by name, ID, description, sensor or sensor type. Partial text is supported and capitalization does not matter.</p>
      <div class="rule-selection-actions"><button type="button" class="button compact" id="select-all-rules" aria-describedby="select-all-scope" disabled>Select all rules</button><p id="select-all-scope" class="hint">Choose a pipeline to select all its rules across every page.</p></div>
      <p id="rule-selection-status" class="hint rule-selection-status" role="status" aria-live="polite"></p>
      <div id="rule-browser" class="rule-browser" hidden><div class="browser-header"><span id="rule-browser-label">AVAILABLE RULES · active and inactive</span><span id="rule-page-label" role="status" aria-live="polite"></span></div><div id="rule-list" class="browser-list"></div><div class="browser-footer"><div class="pagination"><button type="button" class="button compact ghost" id="rules-prev" aria-label="Previous rules page">←</button><button type="button" class="button compact ghost" id="rules-next" aria-label="Next rules page">→</button></div><button type="button" class="button compact" id="add-rules">Add selected rules</button></div></div>
      <details class="paste-rules"><summary>Already have rule IDs?</summary><div class="toolbar"><label class="field" for="pasted-rule-ids"><span>Rule IDs in the selected pipeline</span><textarea id="pasted-rule-ids" rows="2" placeholder="Paste IDs separated by commas or new lines"></textarea></label><button type="button" class="button" id="add-pasted-rules">Add IDs</button></div></details>
      <div id="rule-error" class="inline-error" role="alert" hidden></div>
      <div id="selected-targets"></div>
      <p class="hint" style="margin-top:14px">Inactive rules can be selected. Historical runs ignore maximum photo age; other rule conditions still apply.</p>
    </div></fieldset>
    <section id="limits-section" class="panel">${panelHeader('03', 'Set limits & publication', 'Preview first. Publication starts only when you choose Start.')}<div class="panel-body">
      <div class="field-row">${field('overall-min', 'Overall minimum overlays', '0', 'number', 'min="0" max="100000" value="0" step="1"')}${field('overall-max', 'Overall maximum overlays', 'Use the combined rule maximum', 'number', 'min="1" max="100000" step="1"')}</div>
      <p id="quota-description" class="hint">Shared overlays count once overall and once for each assigned rule. Minimum shortfalls are reported, and available overlays can still run.</p>
      <div class="divider"></div>
      ${publicationPresets()}
      <div class="field-row">${field('batch-size', 'Overlays per batch', '', 'number', 'min="1" max="100" value="10" required step="1"')}${field('delay-ms', 'Delay between batches · ms', '', 'number', 'min="0" max="60000" value="1000" required step="1"')}</div>
      <p class="hint">Publication settings can also be changed while a run is publishing. Updates take effect at a batch boundary.</p>
    </div></section>
    <div id="form-error" class="notice error" role="alert" hidden></div>
    <div class="submit-bar"><div><strong>Review before you run.</strong><p id="preview-description">The preview freezes selected overlays and rule definitions.</p></div><button id="prepare-button" class="button primary" type="submit" disabled>Prepare preview <span aria-hidden="true">↗</span></button></div>
  </form>
  <aside class="configuration-aside">
    <section class="aside-card"><div class="eyebrow">HOW IT WORKS</div><ol class="process-steps"><li><span id="process-find-title">Find & match</span><p id="process-find-description">Fetch historical overlays and evaluate your selected rules.</p></li><li>Review the selection<p id="process-review-description">Inspect overlaps, quotas, and any shortfalls before starting.</p></li><li>Send to the gateway<p>Track confirmed submissions and inspect failed or uncertain items.</p></li></ol></section>
    <section class="aside-card"><h3>Workspace limits</h3><div id="server-limits"><p class="hint" style="margin-top:12px">Loading server options…</p></div><p>Previews and jobs live in this instance’s memory. A restart clears them.</p></section>
    <section class="aside-card history-card"><div class="history-head"><h3>Recent runs</h3><div class="history-actions"><button class="button compact ghost" type="button" id="delete-all-runs" disabled>Delete all</button><button class="button compact ghost" type="button" id="refresh-history" aria-label="Refresh recent runs">↻</button></div></div><p id="history-summary" class="hint"></p><p class="hint history-help">Sizes are retained serialized data, not total RAM. Deleting active runs stops future work; already queued messages remain.</p><p id="history-status" class="hint" role="status" aria-live="polite"></p><div id="history-error" class="inline-error" role="alert" hidden></div><div id="history-list" class="history-list"><p class="hint">Loading recent runs…</p></div></section>
  </aside></div>
  <dialog id="area-map-dialog" class="map-dialog" aria-labelledby="area-map-title" aria-describedby="area-map-instructions">
    <div class="map-dialog-heading"><div><h2 id="area-map-title">Search area map</h2><p id="area-map-instructions">Find your area in the map, then copy its WKT and paste it into Search area after closing this window.</p></div><button id="close-area-map" type="button" class="button ghost" autofocus>Close map</button></div>
    <iframe id="area-map-frame" title="Map for finding the overlay search area" referrerpolicy="no-referrer"></iframe>
    <div class="map-dialog-footer"><p>If the map cannot be displayed here, open it in a new tab.</p><a id="area-map-external" class="button compact ghost" target="_blank" rel="noopener noreferrer">Open map in new tab ↗</a></div>
  </dialog>`;
}

function attachConfiguration() {
  $('preview-form').addEventListener('submit', submitPreview);
  $('open-area-map').addEventListener('click', openAreaMap);
  $('close-area-map').addEventListener('click', () => $('area-map-dialog').close());
  $('area-map-dialog').addEventListener('keydown', event => {
    if (event.key !== 'Tab') return;
    if (event.shiftKey && document.activeElement === $('close-area-map')) {
      event.preventDefault(); $('area-map-external').focus();
    } else if (!event.shiftKey && document.activeElement === $('area-map-external')) {
      event.preventDefault(); $('close-area-map').focus();
    }
  });
  $('area-map-dialog').addEventListener('close', () => {
    $('area-map-frame').removeAttribute('src');
    if (!$('configuration').hidden) $('open-area-map').focus();
  });
  attachPublicationPresets();
  $('sensor-types').addEventListener('change', updateSensorTypeHint);
  document.querySelectorAll('[name="source-mode"]').forEach(radio => radio.addEventListener('change', updateSource));
  document.querySelectorAll('[name="id-processing"]').forEach(radio => radio.addEventListener('change', updateSource));
  $('pipeline-select').addEventListener('change', () => { cancelRuleSelection(); hideError('rule-error'); state.rulesFrom = 0; loadRules(); });
  $('rule-search').addEventListener('input', () => changeRuleSearch(true));
  $('rule-status').addEventListener('change', () => changeRuleSearch(false));
  $('load-rules').addEventListener('click', () => { state.rulesFrom = 0; loadRules(); });
  $('rules-prev').addEventListener('click', () => { state.rulesFrom = Math.max(0, state.rulesFrom - state.rulesSize); loadRules(); });
  $('rules-next').addEventListener('click', () => { state.rulesFrom += state.rulesSize; loadRules(); });
  $('add-rules').addEventListener('click', () => {
    if (state.selectionOperation || state.preparing) return;
    if (!$('pipeline-select').value) return showError('rule-error', new Error('Select a pipeline first.'));
    const selected = [...document.querySelectorAll('[name="rule-choice"]:checked')].map(input => state.rules[Number(input.value)]);
    try { selected.forEach(rule => addTarget($('pipeline-select').value, rule)); renderTargets(); renderRuleList(); } catch (error) { showError('rule-error', error); }
  });
  $('select-all-rules').addEventListener('click', selectAllRules);
  $('add-pasted-rules').addEventListener('click', addPastedRules);
  $('refresh-history').addEventListener('click', loadHistory);
  $('delete-all-runs').addEventListener('click', () => deleteRuns());
  $('history-list').addEventListener('click', event => {
    const button = event.target.closest('[data-delete-run]');
    if (button && !button.disabled) deleteRuns(button.dataset.deleteRun);
  });
  $('selected-targets').addEventListener('input', event => {
    const input = event.target.closest('[data-target-field]');
    if (input) state.targets[Number(input.dataset.targetIndex)][input.dataset.targetField] = input.value;
  });
  $('selected-targets').addEventListener('click', event => {
    const button = event.target.closest('[data-remove-target]');
    if (!button) return;
    state.targets.splice(Number(button.dataset.removeTarget), 1);
    renderTargets();
    renderRuleList();
  });
  renderTargets();
  updateSource();
}

async function loadSetup() {
  await Promise.allSettled([loadOptions(), loadPipelines()]);
}

async function loadOptions() {
  try {
    const options = await api.options();
    state.options = options;
    configureAreaMap();
    configureSensorTypes();
    const { limits, defaults } = options;
    $('batch-size').value = defaults.publication.batchSize;
    $('delay-ms').value = defaults.publication.delayMs;
    syncPublicationPresets();
    for (const [id, range] of [['batch-size', limits.batchSize], ['delay-ms', limits.delayMs]]) {
      $(id).min = range.min; $(id).max = range.max;
    }
    $('overall-max').max = limits.maxSelectedOverlays;
    $('overall-min').max = limits.maxSelectedOverlays;
    updateSource();
    $('server-limits').innerHTML = [
      ['Selected overlays', number(limits.maxSelectedOverlays)], ['Records scanned', number(limits.maxScannedRecords)],
      ['Ready preview', `${limits.readyLifetimeMinutes} min`], ['Active operation', '1 at a time'],
      ...(options.memory ? [['Process memory', `${bytes(options.memory.processBytes)} / ${bytes(options.memory.limitBytes)}`]] : [])
    ].map(([key, value]) => `<div class="limit-line"><span>${key}</span><strong>${value}</strong></div>`).join('');
    $('connection-state').textContent = options.enabled ? 'API connected' : 'Auto-run disabled';
    if (!options.enabled) showError('global-notice', new Error('Auto-run is disabled on this instance. Enable it in the service configuration to prepare runs.'));
  } catch (error) {
    $('connection-state').textContent = 'API unavailable';
    $('area-map-hint').textContent = 'Map settings are unavailable. Reload when the API connection is restored.';
    showError('global-notice', error);
  } finally { syncSelectionControls(); }
}

function configureSensorTypes() {
  const selected = new Set(selectedSensorTypes());
  const catalog = state.options?.provider?.sensorTypeMappings;
  const names = catalog && typeof catalog === 'object' && !Array.isArray(catalog) ? Object.keys(catalog) : [];
  $('sensor-type-options').innerHTML = names.map(name =>
    `<label class="sensor-type-option"><input type="checkbox" name="sensor-type" value="${escape(name)}" ${selected.has(name) ? 'checked' : ''}><span>${escape(name)}</span></label>`).join('');
  updateSensorTypeHint();
}

function selectedSensorTypes() {
  return [...document.querySelectorAll('#sensor-types input:checked')].map(input => input.value);
}

function updateSensorTypeHint() {
  const count = selectedSensorTypes().length;
  $('sensor-types-help').textContent = !$('sensor-type-options').children.length
    ? 'No sensor type choices are configured. Other filters still apply.'
    : count ? `${count} selected. Matches any selected type.` : 'Any sensor type. Select one or more to narrow the search.';
}

function configuredMapUrl() {
  const value = state.options?.ui?.mapUrl;
  if (!value) return null;
  try {
    const url = new URL(value);
    return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
  } catch { return null; }
}

function configureAreaMap() {
  const url = configuredMapUrl();
  $('open-area-map').disabled = !url;
  $('area-map-hint').textContent = url
    ? 'Open the map to find your area and copy its WKT.'
    : 'Map unavailable. Configure AutoRun.Ui.MapUrl to enable it.';
}

function openAreaMap() {
  const url = configuredMapUrl();
  if (!url || $('area-map-dialog').open) return;
  $('area-map-frame').src = url;
  $('area-map-external').href = url;
  $('area-map-dialog').showModal();
}

async function loadPipelines() {
  try {
    const pipelines = await api.pipelines();
    state.pipelines = pipelines;
    $('pipeline-select').innerHTML = '<option value="">Select a pipeline</option>' + pipelines.map(p =>
      `<option value="${escape(p.pipelineId)}" ${!p.enabled ? 'disabled' : ''}>${escape(p.pipelineId)}${p.enabled ? '' : ' · disabled'}</option>`).join('');
    state.setupReady = true;
  } catch (error) {
    $('pipeline-select').innerHTML = '<option value="">Could not load pipelines</option>';
    showError('rule-error', error);
    state.setupReady = false;
  } finally { syncSelectionControls(); }
}

function syncSelectionControls() {
  if (!$('pipeline-select')) return;
  const pipeline = $('pipeline-select').value;
  const busy = Boolean(state.selectionOperation) || state.preparing;
  $('select-all-rules').disabled = !pipeline || !state.options || busy;
  $('select-all-rules').setAttribute('aria-busy', String(state.selectionOperation?.kind === 'all'));
  const filtered = Boolean(state.rulesSearch || state.rulesStatus);
  $('select-all-rules').textContent = filtered ? 'Select all matching rules' : 'Select all rules';
  $('select-all-scope').textContent = pipeline
    ? `All ${filtered ? 'matching ' : ''}rules in ${pipeline}, across every page. ${state.rulesStatus === 'true' ? 'Active only.' : state.rulesStatus === 'false' ? 'Inactive only.' : 'Includes active and inactive rules.'}`
    : 'Choose a pipeline to select all its rules across every page.';
  $('add-rules').disabled = busy || state.rulesLoading || state.rulesPipeline !== pipeline || !state.rules.length;
  $('add-pasted-rules').disabled = busy;
  $('prepare-button').disabled = busy || !state.options?.enabled || (!directSource() && !state.setupReady);
}

function cancelRuleSelection() {
  state.selectionOperation?.controller.abort();
  state.selectionOperation = null;
  if ($('rule-selection-status')) $('rule-selection-status').textContent = '';
  syncSelectionControls();
}

function beginRuleSelection(kind, pipeline) {
  if (state.selectionOperation || state.preparing) return null;
  const operation = { kind, pipeline, search: state.rulesSearch, status: state.rulesStatus, controller: new AbortController() };
  state.selectionOperation = operation;
  hideError('rule-error');
  $('rule-selection-status').textContent = kind === 'all' ? `Loading all ${operation.search || operation.status ? 'matching ' : ''}rules in ${pipeline}…` : '';
  syncSelectionControls();
  return operation;
}

function isCurrentSelection(operation) {
  return state.selectionOperation === operation && $('pipeline-select')?.value === operation.pipeline;
}

function finishRuleSelection(operation) {
  if (state.selectionOperation !== operation) return;
  state.selectionOperation = null;
  syncSelectionControls();
}

async function selectAllRules() {
  const pipeline = $('pipeline-select').value;
  if (!pipeline || !state.options) return;
  const operation = beginRuleSelection('all', pipeline);
  if (!operation) return;
  const maxTargets = state.options.limits.maxTargets ?? 100;
  const candidates = new Map();
  const additions = () => [...candidates.values()].filter(rule =>
    !state.targets.some(target => target.pipelineId === pipeline && target.ruleId === rule._id));
  const checkCapacity = () => {
    const newRules = additions();
    if (state.targets.length + newRules.length > maxTargets)
      throw new Error(`Selecting every rule would exceed the ${maxTargets}-target limit across all pipelines. Remove targets or select rules individually.`);
    return newRules;
  };
  try {
    // The API has offset pagination without a total. Read through the final page;
    // bound repeated/overlapping pages if the catalog changes during retrieval.
    for (let from = 0; ; from += state.rulesSize) {
      if (from >= Math.max(1000, maxTargets * 10)) throw new Error('The rule list kept changing while loading. Try again.');
      const page = await api.rules(pipeline, from, state.rulesSize, operation.controller.signal, operation.search, operation.status);
      if (!isCurrentSelection(operation)) return;
      if (!Array.isArray(page) || page.some(rule => !rule || typeof rule._id !== 'string' || !rule._id.trim()))
        throw new Error('The API returned an invalid rule list.');
      for (const rule of page) candidates.set(rule._id, rule);
      checkCapacity();
      if (page.length < state.rulesSize) break;
    }
    if (!isCurrentSelection(operation)) return;
    const newRules = checkCapacity();
    // Commit only after every page succeeds. Keep existing target objects and quotas.
    state.targets.push(...newRules.map(rule => ({ pipelineId: pipeline, ruleId: rule._id, minOverlays: '0', maxOverlays: '100', rule })));
    renderTargets();
    if (!state.rulesLoading) renderRuleList();
    const existing = candidates.size - newRules.length;
    $('rule-selection-status').textContent = !candidates.size ? `No rules found in ${pipeline}.`
      : !newRules.length ? `All ${candidates.size} rules in ${pipeline} are already selected.`
      : `Added ${newRules.length} rule${newRules.length === 1 ? '' : 's'} from ${pipeline}.${existing ? ` ${existing} already selected; existing quotas kept.` : ''}`;
  } catch (error) {
    if (!isCurrentSelection(operation)) return;
    $('rule-selection-status').textContent = '';
    showError('rule-error', Object.assign(new Error(), error, { message: `No rules were added. ${error.message}` }));
  } finally { finishRuleSelection(operation); }
}

function updateSource() {
  const mode = document.querySelector('[name="source-mode"]:checked').value;
  const direct = directSource();
  if (direct) cancelRuleSelection();
  hideError('form-error');
  $('overlay-ids-field').hidden = mode !== 'OverlayIds';
  $('overlay-ids').disabled = mode !== 'OverlayIds';
  $('filter-details').hidden = mode === 'OverlayIds';
  $('filter-details').querySelectorAll('input, select, textarea').forEach(input => { input.disabled = mode === 'OverlayIds'; });
  $('filter-details').open = mode === 'Filters';
  $('rule-selection').hidden = direct;
  $('rule-selection').disabled = direct;
  $('limits-section').querySelector('.step-number').textContent = direct ? '02' : '03';
  const filterMaximum = state.options?.defaults.filterMaxOverlays ?? 100;
  $('overall-max').placeholder = mode === 'Filters' ? `Default: ${number(filterMaximum)} overlays`
    : direct ? 'Use all supplied overlay IDs' : 'Use the combined rule maximum';
  $('quota-description').textContent = direct
    ? `Each selected overlay counts once. ${mode === 'Filters' ? `Leave maximum empty to select up to ${number(filterMaximum)} overlays. ` : ''}Minimum shortfalls are reported, and available overlays can still run.`
    : 'Shared overlays count once overall and once for each assigned rule. Minimum shortfalls are reported, and available overlays can still run.';
  $('preview-description').textContent = direct
    ? 'The preview freezes selected overlays. The gateway uses current rules when it receives them.'
    : 'The preview freezes selected overlays and rule definitions.';
  $('process-find-title').textContent = direct ? 'Find overlays' : 'Find & match';
  $('process-find-description').textContent = mode === 'Filters' ? 'Search Elastic Provider using your filters.'
    : direct ? 'Fetch the supplied overlay IDs from Elastic Provider.' : 'Fetch historical overlays and evaluate your selected rules.';
  $('process-review-description').textContent = direct ? 'Inspect selected overlays, overall limits, and any shortfalls before starting.' : 'Inspect overlaps, quotas, and any shortfalls before starting.';
  $('filters-description').textContent = mode === 'Filters' ? 'Empty filters search all overlays' : 'Optional for rule-derived searches';
  $('source-description').textContent = {
    Rules: 'Search conditions are derived from the rules you select below. Additional filters can narrow the search.',
    Filters: 'Find overlays using the filters below and send them directly to the Unified Gateway. The gateway applies its current rules, including photo-age limits.',
    OverlayIds: direct ? 'Fetch overlays by ID and send them directly to the Unified Gateway. The gateway decides which pipelines and current rules apply.'
      : 'Fetch these overlays by ID, then match them against your selected pipeline rules.'
  }[mode];
  syncSelectionControls();
}

function ruleDescription(rule) {
  const match = rule.match || {};
  const parts = [];
  if (rule.matchAll) parts.push('Matches all overlays');
  if (match.sensors?.length) parts.push(match.sensors.map(s => `${s.name}${s.registrationQualities?.length ? ` · ${s.registrationQualities.join('/')}` : ''}${s.gridTypes?.length ? ` · ${s.gridTypes.join('/')}` : ''}`).join('; '));
  if (match.sensorTypes?.length) parts.push(`Sensor types: ${match.sensorTypes.join(', ')}`);
  if (match.resolution) parts.push(`Resolution ${match.resolution.min ?? 'any'}–${match.resolution.max ?? 'any'}`);
  if (match.locationWkt) parts.push('Geographic area');
  if (match.modeSAR?.length) parts.push(`SAR: ${match.modeSAR.join(', ')}`);
  if (match.maxPhotoAgeDays) parts.push(`${match.maxPhotoAgeDays}-day age limit ignored for historical runs`);
  return parts.join(' · ') || 'No additional match constraints';
}

async function loadRules() {
  clearTimeout(state.rulesTimer);
  state.rulesController?.abort();
  const controller = new AbortController();
  state.rulesController = controller;
  const pipeline = $('pipeline-select').value;
  const version = ++state.rulesVersion;
  state.rulesLoading = Boolean(pipeline);
  syncSelectionControls();
  if (!pipeline) {
    state.rules = [];
    $('rule-browser').hidden = true;
    return;
  }
  $('rule-browser').hidden = false;
  $('rule-list').innerHTML = '<div class="loading"><span class="spinner"></span>Loading rules…</div>';
  $('add-rules').disabled = true;
  $('rules-next').disabled = true;
  $('rules-prev').disabled = true;
  hideError('rule-error');
  try {
    const rules = await api.rules(pipeline, state.rulesFrom, state.rulesSize, controller.signal, state.rulesSearch, state.rulesStatus);
    if (version !== state.rulesVersion) return;
    state.rules = rules;
    state.rulesPipeline = pipeline;
    state.rulesLoading = false;
    renderRuleList();
  } catch (error) {
    if (version !== state.rulesVersion || error.name === 'AbortError') return;
    state.rules = [];
    state.rulesLoading = false;
    $('rule-list').innerHTML = '<div class="run-empty">Rules could not be loaded. Try Browse rules again.</div>';
    showError('rule-error', error);
  } finally { if (version === state.rulesVersion) syncSelectionControls(); }
}

function changeRuleSearch(debounce) {
  cancelRuleSelection();
  clearTimeout(state.rulesTimer);
  state.rulesController?.abort();
  state.rulesVersion++;
  state.rulesFrom = 0;
  state.rulesSearch = $('rule-search').value.trim();
  state.rulesStatus = $('rule-status').value;
  state.rulesLoading = Boolean($('pipeline-select').value);
  syncSelectionControls();
  if (state.rulesLoading) {
    $('rule-browser').hidden = false;
    $('rule-list').innerHTML = '<div class="loading"><span class="spinner"></span>Searching rules…</div>';
    $('rules-next').disabled = true;
    $('rules-prev').disabled = true;
  }
  if (debounce) state.rulesTimer = setTimeout(loadRules, 300);
  else loadRules();
}

function renderRuleList() {
  const pipeline = $('pipeline-select').value;
  if (state.rulesLoading || state.rulesPipeline !== pipeline) { syncSelectionControls(); return; }
  $('rule-page-label').textContent = state.rules.length ? `${state.rulesFrom + 1}–${state.rulesFrom + state.rules.length}` : 'No rules';
  $('rule-browser-label').textContent = `${state.rulesSearch ? 'MATCHING' : 'AVAILABLE'} RULES · ${state.rulesStatus === 'true' ? 'active' : state.rulesStatus === 'false' ? 'inactive' : 'active and inactive'}`;
  $('rule-list').innerHTML = state.rules.length ? state.rules.map((rule, i) => {
    const exists = state.targets.some(target => target.pipelineId === pipeline && target.ruleId === rule._id);
    return `<label class="rule-choice"><input type="checkbox" name="rule-choice" value="${i}" ${exists ? 'disabled' : ''}><div><div class="rule-choice-name">${escape(rule.ruleName)}${exists ? ' · added' : ''}</div><div class="rule-choice-meta">${escape(ruleDescription(rule))}</div><div class="cell-sub mono">${escape(rule._id)}</div></div>${badge(rule.isActive ? 'Active' : 'Inactive')}</label>`;
  }).join('') : '<div class="run-empty">No rules on this page.</div>';
  $('rules-prev').disabled = state.rulesFrom === 0;
  $('rules-next').disabled = state.rules.length < state.rulesSize;
  syncSelectionControls();
}

function addTarget(pipelineId, rule) {
  if (state.targets.some(target => target.pipelineId === pipelineId && target.ruleId === rule._id)) return;
  if (state.targets.length >= (state.options?.limits.maxTargets ?? 100)) throw new Error(`A preview supports at most ${state.options?.limits.maxTargets ?? 100} rule targets.`);
  state.targets.push({ pipelineId, ruleId: rule._id, minOverlays: '0', maxOverlays: '100', rule });
}

async function addPastedRules() {
  if (state.selectionOperation || state.preparing) return;
  hideError('rule-error');
  const pipeline = $('pipeline-select').value;
  const ids = splitValues($('pasted-rule-ids').value);
  if (!pipeline || !ids.length) return showError('rule-error', new Error('Select a pipeline and enter at least one rule ID.'));
  if (ids.length > (state.options?.limits.maxTargets ?? 100)) return showError('rule-error', new Error('Too many rule IDs for one preview.'));
  const operation = beginRuleSelection('ids', pipeline);
  if (!operation) return;
  try {
    const errors = [];
    // Limit lookup concurrency and show individual failures without losing successful selections.
    for (let i = 0; i < ids.length; i += 5) {
      const results = await Promise.allSettled(ids.slice(i, i + 5).map(id => api.rule(pipeline, id, operation.controller.signal)));
      if (!isCurrentSelection(operation)) return;
      results.forEach((result, j) => {
        if (result.status === 'fulfilled') addTarget(pipeline, result.value);
        else errors.push(Object.assign(new Error(), result.reason, { message: `${ids[i + j]}: ${result.reason.message}` }));
      });
      // Successful lookups remain selected if a later batch is cancelled.
      // Keep those committed targets visible before starting the next batch.
      renderTargets(); renderRuleList();
    }
    if (errors.length) showError('rule-error', { message: `${errors.length} rule lookup${errors.length === 1 ? '' : 's'} failed. Successful selections were kept.`, causes: errors });
    else $('pasted-rule-ids').value = '';
  } catch (error) { if (isCurrentSelection(operation)) { renderTargets(); renderRuleList(); showError('rule-error', error); } }
  finally { finishRuleSelection(operation); }
}

function renderTargets() {
  $('selected-targets').innerHTML = !state.targets.length
    ? '<div class="targets-empty">No rules selected yet.<br>Choose a pipeline to browse its rules, or paste rule IDs.</div>'
    : `<div class="section-label" style="margin-top:22px"><h3>Selected targets</h3><span class="selection-count">${state.targets.length} rule${state.targets.length === 1 ? '' : 's'}</span></div><div class="table-wrap"><table class="target-table"><thead><tr><th>Rule / pipeline</th><th>Minimum</th><th>Maximum</th><th><span class="visually-hidden">Actions</span></th></tr></thead><tbody>${state.targets.map((target, i) => `<tr><td>${escape(target.rule.ruleName)}<span class="cell-sub">${escape(target.pipelineId)}${target.rule.isActive ? '' : ' · inactive'}</span></td><td><input type="number" min="0" max="100000" step="1" required value="${escape(target.minOverlays)}" data-target-index="${i}" data-target-field="minOverlays" aria-label="Minimum for ${escape(target.rule.ruleName)}"></td><td><input type="number" min="1" max="100000" step="1" required value="${escape(target.maxOverlays)}" data-target-index="${i}" data-target-field="maxOverlays" aria-label="Maximum for ${escape(target.rule.ruleName)}"></td><td><button type="button" class="button icon ghost" data-remove-target="${i}" aria-label="Remove ${escape(target.rule.ruleName)}">×</button></td></tr>`).join('')}</tbody></table></div>`;
}

function optionalNumber(id) {
  const value = $(id).value.trim();
  if (!value) return undefined;
  const parsed = Number(value);
  if (!Number.isFinite(parsed)) throw new Error(`Enter a valid number for ${$(id).labels[0].textContent.trim()}.`);
  return parsed;
}

function timeValue(id) {
  const value = $(id).value;
  if (!value) return undefined;
  const date = new Date(value);
  if (!Number.isFinite(date.getTime())) throw new Error('Enter a valid date and time.');
  const sameLocal = candidate => {
    const pad = n => String(n).padStart(2, '0');
    return `${candidate.getFullYear()}-${pad(candidate.getMonth() + 1)}-${pad(candidate.getDate())}T${pad(candidate.getHours())}:${pad(candidate.getMinutes())}` === value.slice(0, 16);
  };
  if (!sameLocal(date)) throw new Error('This local time does not exist because of a daylight-saving change. Choose another time.');
  if ([-120, -60, -30, 30, 60, 120].some(minutes => sameLocal(new Date(date.getTime() + minutes * 60000))))
    throw new Error('This local time is ambiguous because of a daylight-saving change. Choose a time outside the repeated hour.');
  return date.toISOString();
}

function collectFilters() {
  const result = {};
  const sensorNames = splitValues($('sensor-names').value);
  if (sensorNames.length) result.sensorNames = sensorNames;
  const sensorTypes = selectedSensorTypes();
  if (sensorTypes.length) result.sensorTypes = sensorTypes;
  if ($('registration-quality').value) result.registrationQualities = $('registration-quality').value.split(',');
  if ($('is-realtime').value !== '') result.isRealtime = $('is-realtime').value === 'true';
  const min = optionalNumber('resolution-min'), max = optionalNumber('resolution-max');
  if (min !== undefined || max !== undefined) {
    if ((min !== undefined && min <= 0) || (max !== undefined && max <= 0) || (min !== undefined && max !== undefined && min > max))
      throw new Error('Resolution bounds must be positive, with the minimum no greater than the maximum.');
    result.resolution = { min, max };
  }
  const wkt = $('area-wkt').value.trim();
  if (wkt) {
    if (!/^(?:POLYGON|MULTIPOLYGON)\s*\(/i.test(wkt)) throw new Error('The search area must be a WKT Polygon or MultiPolygon.');
    result.wkt = wkt;
  }
  for (const [prefix, key] of [['created', 'createdTime'], ['photo', 'photoTime']]) {
    const from = timeValue(`${prefix}-from`), to = timeValue(`${prefix}-to`);
    if (from && to && from > to) throw new Error(`${prefix === 'created' ? 'Creation' : 'Photo'} time must start before it ends.`);
    if (from || to) result[key] = { from, to };
  }
  return result;
}

async function submitPreview(event) {
  event.preventDefault();
  const direct = directSource();
  if (state.selectionOperation || state.preparing || !state.options?.enabled || (!direct && !state.setupReady)) return;
  hideError('form-error');
  const button = $('prepare-button');
  try {
    if (!direct && !state.targets.length) throw new Error('Select at least one pipeline rule before preparing a preview.');
    const targets = (direct ? [] : state.targets).map(target => {
      const min = Number(target.minOverlays), max = Number(target.maxOverlays);
      if (!Number.isInteger(min) || !Number.isInteger(max) || target.minOverlays === '' || target.maxOverlays === '' || min < 0 || max < 1 || max < min || max > 100000)
        throw new Error(`Check the overlay limits for ${target.rule.ruleName}: maximum is required and must be at least the minimum.`);
      return { pipelineId: target.pipelineId, ruleId: target.ruleId, minOverlays: min, maxOverlays: max };
    });
    const mode = document.querySelector('[name="source-mode"]:checked').value;
    const source = { mode };
    if (mode === 'OverlayIds') {
      source.overlayIds = splitValues($('overlay-ids').value);
      if (!source.overlayIds.length) throw new Error('Enter at least one overlay ID.');
    } else {
      const filters = collectFilters();
      if (Object.keys(filters).length) source.filters = filters;
    }
    const min = optionalNumber('overall-min') ?? 0, max = optionalNumber('overall-max');
    if (max !== undefined && min > max) throw new Error('The overall minimum cannot be greater than the overall maximum.');
    state.preparing = true;
    syncSelectionControls();
    button.textContent = 'Preparing…';
    const run = await api.preview({ source, targets, minOverlays: min, maxOverlays: max,
      publication: { batchSize: Number($('batch-size').value), delayMs: Number($('delay-ms').value) } });
    navigate(`/ui/auto-run/runs/${encodeURIComponent(run.id)}`);
  } catch (error) {
    showError('form-error', error);
    $('form-error').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  } finally {
    state.preparing = false;
    syncSelectionControls();
    button.innerHTML = 'Prepare preview <span aria-hidden="true">↗</span>';
  }
}

async function loadHistory() {
  clearTimeout(state.historyTimer);
  const version = ++state.historyVersion;
  try {
    const runs = await api.list();
    if (version !== state.historyVersion || !$('history-list')) return;
    const hadPending = state.historyPendingIds.size > 0;
    state.history = [...runs].sort((a, b) => b.createdAt.localeCompare(a.createdAt));
    state.historyPendingIds = new Set(runs.filter(run => run.deletionRequested || state.historyPendingIds.has(run.id)).map(run => run.id));
    if (hadPending && !state.historyPendingIds.size) $('history-status').textContent = 'Pending deletions finished. Their retained data has been released.';
    renderHistory();
  } catch (error) {
    if (version !== state.historyVersion || !$('history-list')) return;
    showError('history-error', error);
  } finally {
    if (version === state.historyVersion && state.historyPendingIds.size && $('configuration') && !$('configuration').hidden && !$('app').hidden)
      state.historyTimer = setTimeout(loadHistory, 2000);
  }
}

function renderHistory() {
  const runs = state.history;
  const focusedDelete = document.activeElement?.dataset.deleteRun;
  const focusedLink = document.activeElement?.closest('.history-link')?.getAttribute('href');
  const scrollTop = $('history-list').scrollTop;
  $('delete-all-runs').disabled = !runs.length || state.historyDeleting || runs.every(run => state.historyPendingIds.has(run.id));
  $('refresh-history').disabled = state.historyDeleting;
  $('history-summary').textContent = `${number(runs.length)} retained run${runs.length === 1 ? '' : 's'} · ${bytes(runs.reduce((total, run) => total + (run.retainedBytes ?? 0), 0))} retained data`;
  $('history-list').innerHTML = runs.length ? runs.map(run => {
    const pending = state.historyPendingIds.has(run.id);
    return `<article class="history-item"><a href="/ui/auto-run/runs/${encodeURIComponent(run.id)}" data-route class="history-link" aria-label="Open run ${escape(run.id)}"><div class="history-top"><span class="history-id">${escape(run.id)}</span>${badge(pending ? 'Deleting' : run.state)}</div>${directRun(run) ? '<div class="history-mode">Direct to gateway</div>' : ''}<div class="history-meta"><span>${number(run.counters.selected)} overlays</span><span>${escape(formatTime(run.createdAt))}</span></div></a><div class="history-storage"><span>${bytes(run.retainedBytes ?? 0)} retained</span><button type="button" class="button compact ghost" data-delete-run="${escape(run.id)}" aria-label="Delete run ${escape(run.id)}" ${state.historyDeleting || pending ? 'disabled' : ''}>${pending ? 'Deleting…' : 'Delete'}</button></div>${pending ? '<p class="hint">Stopping work before releasing retained data…</p>' : ''}</article>`;
  }).join('') : '<p class="hint">No retained runs yet. Your first preview will appear here.</p>';
  $('history-list').scrollTop = scrollTop;
  if (focusedDelete || focusedLink) {
    const replacement = focusedDelete
      ? [...$('history-list').querySelectorAll('[data-delete-run]')].find(button => button.dataset.deleteRun === focusedDelete && !button.disabled)
      : [...$('history-list').querySelectorAll('.history-link')].find(link => link.getAttribute('href') === focusedLink);
    const fallback = $('history-summary');
    fallback.tabIndex = -1;
    (replacement ?? fallback).focus({ preventScroll: true });
  }
}

async function deleteRuns(id = null) {
  if (state.historyDeleting || (id && state.historyPendingIds.has(id))) return;
  state.historyDeleting = true;
  clearTimeout(state.historyTimer);
  state.historyVersion++;
  hideError('history-error');
  $('history-status').textContent = id ? 'Deleting run…' : 'Deleting all retained runs…';
  renderHistory();
  try {
    const result = await (id ? api.deleteRun(id) : api.deleteAllRuns());
    state.history = state.history.filter(run => !result.deletedIds.includes(run.id));
    for (const deleted of result.deletedIds) state.historyPendingIds.delete(deleted);
    for (const pending of result.pendingIds) state.historyPendingIds.add(pending);
    $('history-status').textContent = `${number(result.deletedIds.length)} deleted · ${bytes(result.releasedRetainedBytes)} retained data released.${result.pendingIds.length ? ` ${number(result.pendingIds.length)} awaiting in-flight work to stop.` : ''}`;
  } catch (error) { showError('history-error', error); }
  finally {
    state.historyDeleting = false;
    renderHistory();
    await loadHistory();
  }
}

function detailMarkup(id) {
  return `<a class="back-link" href="/ui/auto-run" data-route>← Back to configuration</a>
  <div class="page-heading run-heading"><div><div class="eyebrow">RUN WORKSPACE</div><h1>Review. Launch. Follow through.</h1><div class="run-id mono">${escape(id)}</div></div><div class="run-heading-actions"><span id="run-state">${badge('Loading')}</span><button id="cancel-run" type="button" class="button danger" hidden>Cancel</button><button id="rerun-run" type="button" class="button" title="Prepare a fresh preview using the original configuration and saved publication settings. Rules are read again; publication requires Start." hidden>Rerun</button><button id="start-run" type="button" class="button primary" hidden>Start run ↗</button></div></div>
  <div id="run-error" class="notice error" role="alert" hidden></div>
  <div id="run-start-note" class="notice warning" role="status" hidden></div>
  <div id="run-outcome-note" class="notice success" role="status" hidden></div>
  <div id="run-notices" hidden></div>
  <div id="run-content" hidden>
    <div class="stat-grid">${[['selected', 'Selected', ''], ['confirmed', 'Confirmed', 'good'], ['failed', 'Failed', 'bad'], ['uncertain', 'Uncertain', 'warning'], ['remaining', 'Remaining', '']].map(([key, label, style]) =>
      `<div class="stat ${style}"><div class="stat-label">${label}</div><div class="stat-value" id="count-${key}">0</div></div>`).join('')}</div>
    <section class="panel"><div class="progress-block"><div class="progress-text"><span id="progress-label"></span><span id="progress-value" class="mono"></span></div><div id="run-progress" class="progress-track" role="progressbar" aria-label="Publication progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><div id="progress-fill"></div></div><p id="provider-progress" class="provider-progress" role="status" aria-live="polite" hidden></p><div class="run-meta" id="run-meta"></div><p id="run-memory" class="memory-status hint" hidden></p></div></section>
    <div class="run-layout"><section class="panel"><div class="panel-header"><div><h2 id="run-target-heading">Rule assignments</h2><p id="run-target-description">Exact counts for this frozen selection.</p></div></div><div id="direct-run-note" class="panel-body" hidden><p>The gateway chooses pipelines and applies its current rules, including photo-age limits. This preview freezes overlay data only.</p><p id="direct-run-limits" class="hint"></p></div><div id="run-target-table" class="table-wrap"><table><thead><tr><th>Rule / pipeline</th><th>Min / max</th><th>Selected</th><th>Confirmed</th><th>Failed</th><th>Uncertain</th><th>Shortfall</th></tr></thead><tbody id="run-targets"></tbody></table></div></section>
    <section class="panel run-publication"><div class="panel-header"><div><h2>Publication settings</h2><p>Control the pace of this run.</p></div></div><form id="publication-form" class="panel-body">${publicationPresets('run-')}<div class="field-row">${field('run-batch-size', 'Batch size', '', 'number', 'min="1" max="100" required step="1"')}${field('run-delay-ms', 'Delay · ms', '', 'number', 'min="0" max="60000" required step="1"')}</div><button class="button full" id="update-publication" type="submit">Apply settings</button><p id="publication-status" class="publication-status" role="status"></p><p class="hint">Changes apply at a batch boundary. Confirmed means queued to the gateway, not processed downstream.</p></form></section></div>
    <section class="panel"><div class="panel-header items-toolbar"><div><h2>Selected overlays</h2><p id="items-description">Assigned targets and publication outcomes.</p></div><label class="field" for="item-outcome"><span class="visually-hidden">Filter items by outcome</span><select id="item-outcome"><option value="">All outcomes</option>${['Pending', 'InFlight', 'Confirmed', 'Failed', 'Uncertain'].map(v => `<option>${v}</option>`).join('')}</select></label></div><div class="table-wrap"><table class="items-table"><thead><tr><th>Overlay / sensor</th><th>Photo time</th><th id="items-target-heading">Assigned rules</th><th>Outcome</th></tr></thead><tbody id="run-items"></tbody></table></div><div class="browser-footer"><span id="items-count" class="hint"></span><div class="pagination"><button id="items-prev" class="button compact ghost" type="button">← Previous</button><button id="items-next" class="button compact ghost" type="button">Next →</button></div></div></section>
    <details class="panel"><summary class="panel-body" style="cursor:pointer;font-size:12px">Provider queries · frozen search</summary><div class="panel-body" style="padding-top:0"><pre id="provider-queries" class="mono" style="white-space:pre-wrap;overflow-wrap:anywhere;font-size:11px;color:#a7bbc9"></pre></div></details>
  </div><div id="run-loading" class="loading"><span class="spinner"></span>Loading run…</div>`;
}

function attachDetail() {
  $('start-run').addEventListener('click', () => runAction('start'));
  $('cancel-run').addEventListener('click', () => runAction('cancel'));
  $('rerun-run').addEventListener('click', rerun);
  $('item-outcome').addEventListener('change', () => { state.itemsOutcome = $('item-outcome').value; state.itemsFrom = 0; loadItems(); });
  $('items-prev').addEventListener('click', () => { state.itemsFrom = Math.max(0, state.itemsFrom - state.itemsSize); loadItems(); });
  $('items-next').addEventListener('click', () => { state.itemsFrom += state.itemsSize; loadItems(); });
  $('publication-form').addEventListener('input', () => { state.publicationDirty = true; });
  $('publication-form').addEventListener('submit', updatePublication);
  attachPublicationPresets('run-');
}

async function refreshRun(manual = false) {
  const id = state.runId;
  if (!id) return;
  clearTimeout(state.timer);
  const version = ++state.refreshVersion;
  try {
    const run = await api.run(id);
    if (id !== state.runId || version !== state.refreshVersion) return;
    state.run = run;
    if ($('run-error').dataset.source === 'poll') hideError('run-error');
    $('run-loading').hidden = true;
    $('run-content').hidden = false;
    renderRun(run);
    await loadItems();
    if (id === state.runId && version === state.refreshVersion && (!terminal(run.state) || run.deletionRequested)) {
      state.timer = setTimeout(() => refreshRun(), run.state === 'Ready' && !run.deletionRequested ? 5000 : 2000);
    }
  } catch (error) {
    if (id !== state.runId || version !== state.refreshVersion) return;
    $('run-loading').hidden = true;
    if (error.status === 404) {
      state.run = null;
      $('run-content').hidden = true;
      for (const control of ['start-run', 'cancel-run', 'rerun-run', 'run-start-note', 'run-outcome-note', 'run-notices']) $(control).hidden = true;
    }
    showError('run-error', error.status === 404 ? Object.assign(new Error(), error, { message: 'This run is no longer available. It may have been deleted, expired, or lost when the service restarted. Return to configuration to prepare a new preview.' }) : error, 'poll');
    if (error.status !== 404 && (!state.run || !terminal(state.run.state))) state.timer = setTimeout(() => refreshRun(), 5000);
  }
}

function renderRunActions(run) {
  const pending = pendingRunActions.get(run.id);
  const emptyReady = run.state === 'Ready' && run.counters.selected === 0;
  $('start-run').hidden = run.state !== 'Ready';
  $('start-run').disabled = run.state !== 'Ready' || run.counters.selected === 0 || Boolean(pending) || run.deletionRequested;
  $('start-run').textContent = pending === 'start' ? 'Starting…' : 'Start run ↗';
  $('rerun-run').hidden = ['Preparing', 'Publishing'].includes(run.state);
  $('rerun-run').disabled = state.rerunPending || Boolean(pending) || run.deletionRequested;
  $('cancel-run').hidden = terminal(run.state);
  $('cancel-run').disabled = Boolean(pending) || run.deletionRequested;
  const reason = run.deletionRequested ? 'Deletion requested. Future work is cancelled; retained data will be released after in-flight work finishes.' : emptyReady
    ? 'Start is unavailable because this preview contains no selected overlays. Review the search notes and invalid-record counts, then adjust the criteria or provider settings and prepare a new preview.'
    : pending === 'start' ? 'Starting the run. Waiting for Rules API to confirm the request.' : '';
  $('run-start-note').textContent = reason;
  $('run-start-note').hidden = !reason;
  $('run-start-note').classList.toggle('warning', emptyReady);
  if (reason) {
    $('start-run').setAttribute('aria-describedby', 'run-start-note');
    $('start-run').title = reason;
  } else {
    $('start-run').removeAttribute('aria-describedby');
    $('start-run').removeAttribute('title');
  }
}

function renderRun(run) {
  const direct = directRun(run);
  $('run-state').innerHTML = badge(run.deletionRequested ? 'Deleting' : run.state);
  renderRunActions(run);
  for (const key of ['selected', 'confirmed', 'failed', 'uncertain', 'remaining']) $(`count-${key}`).textContent = number(run.counters[key]);
  const complete = run.counters.confirmed + run.counters.failed + run.counters.uncertain;
  const percent = run.counters.selected ? Math.min(100, Math.round(complete / run.counters.selected * 100)) : 0;
  $('progress-fill').style.width = `${percent}%`;
  $('run-progress').setAttribute('aria-valuenow', String(percent));
  $('progress-value').textContent = run.state === 'Preparing' ? `${number(run.counters.scanned)} scanned` : `${percent}%`;
  $('progress-label').textContent = {
    Preparing: direct ? 'Fetching overlays…' : 'Fetching and matching overlays…',
    Ready: run.counters.selected > 0 ? 'Preview ready · review the selection, then start' : 'Preparation finished · no overlays selected',
    Publishing: `Publishing · ${Number(run.confirmedPerSecond || 0).toFixed(1)} confirmed/s`,
    Completed: 'Publication complete · submissions confirmed by RabbitMQ',
    CompletedWithErrors: 'Publication finished · inspect failed and uncertain items',
    Failed: 'Run failed · inspect the errors below', Cancelled: 'Cancelled · queued messages remain queued',
    Expired: 'Preview expired · prepare a new run'
  }[run.state] || run.state;
  renderProviderProgress(run);
  const outcome = run.state === 'Ready' && run.counters.selected > 0 && !run.stoppedReason
    ? `Preview ready: ${number(run.counters.selected)} overlays selected from ${number(run.counters.scanned)} records scanned. Review the selection, then choose Start run.`
    : run.state === 'Completed'
      ? `Publication complete: ${number(run.counters.confirmed)} submissions confirmed by RabbitMQ. Confirmed means queued to the gateway; downstream processing is not tracked here.`
      : run.state === 'CompletedWithErrors'
        ? `Publication finished: ${number(run.counters.confirmed)} confirmed, ${number(run.counters.failed)} failed, ${number(run.counters.uncertain)} uncertain. Inspect item outcomes before deciding whether to prepare another run; uncertain messages may already be queued.`
        : '';
  $('run-outcome-note').textContent = outcome;
  $('run-outcome-note').hidden = !outcome;
  $('run-outcome-note').classList.toggle('warning', run.state === 'CompletedWithErrors');
  $('run-outcome-note').classList.toggle('success', run.state !== 'CompletedWithErrors');
  $('run-meta').innerHTML = [
    ...(direct ? ['Direct to gateway'] : []), `Scanned ${number(run.counters.scanned)}`,
    ...(!direct ? [`Observed matches ${number(run.counters.observedMatches)}`] : []),
    `Duplicates ${number(run.counters.duplicates)}`, `Invalid ${number(run.counters.invalid)}`,
    `In flight ${number(run.counters.inFlight)}`, `Source ${run.sourceExhausted ? 'exhausted' : 'not exhausted'}`,
    `Created ${formatTime(run.createdAt)}`, ...(run.expiresAt ? [`Expires ${formatTime(run.expiresAt)}`] : []),
    `Retained data ${bytes(run.retainedBytes ?? 0)}`
  ].map(value => `<span>${escape(value)}</span>`).join('');
  $('run-memory').hidden = !run.memory;
  if (run.memory) {
    $('run-memory').textContent = `Process memory: ${bytes(run.memory.processBytes)} / ${bytes(run.memory.limitBytes)} safeguard limit · Headroom: ${bytes(run.memory.headroomBytes)}${run.memory.isUnderPressure ? ' · Memory is near the safeguard limit.' : ''}`;
    $('run-memory').classList.toggle('under-pressure', Boolean(run.memory.isUnderPressure));
  }
  const diagnostics = Array.isArray(run.diagnostics) ? run.diagnostics : [];
  const diagnosticMessages = new Set(diagnostics.map(diagnostic => diagnostic.message));
  const warnings = (run.warnings ?? []).filter(message => !diagnosticMessages.has(message));
  const errors = (run.errors ?? []).filter(message => !diagnosticMessages.has(message));
  if (run.shortfall > 0) warnings.unshift(`Overall minimum shortfall: ${number(run.shortfall)} overlays. ${run.counters.selected > 0 ? 'The available selection can still run.' : 'No selected overlays are available to run.'}`);
  const limitedSearch = !run.sourceExhausted && ({
    memoryLimit: 'Scanning stopped to protect service memory.',
    payloadBudget: 'Scanning stopped at the retained payload budget.',
    responseLimit: 'Scanning stopped because a provider page exceeded the response size limit.',
    providerUnavailable: 'Provider search stopped after all retry attempts failed.'
  })[run.stoppedReason];
  if (run.stoppedReason && !run.sourceExhausted) warnings.push(`${limitedSearch || `Search stopped: ${run.stoppedReason}.`} Observed counts are not a complete source total.`);
  const partialReady = run.stoppedReason && !run.sourceExhausted && run.state === 'Ready' && run.counters.selected > 0;
  const expandedDiagnostics = new Set([...$('run-notices').querySelectorAll('[data-diagnostic-key][open]')].map(element => element.dataset.diagnosticKey));
  $('run-notices').hidden = !warnings.length && !errors.length && !diagnostics.length && !run.diagnosticsOmitted && !partialReady;
  $('run-notices').innerHTML = [
    partialReady ? `<div id="partial-selection-note" class="notice warning" role="status"><strong>Partial selection ready</strong>${number(run.counters.selected)} selected overlays were retained. You can start this selection now. Results may be incomplete and minimum quotas may not be met.</div>` : '',
    warnings.length ? `<div class="notice warning"><strong>Selection & run notes</strong><ul class="message-list">${warnings.map(w => `<li>${escape(w)}</li>`).join('')}</ul></div>` : '',
    errors.length ? `<div class="notice error"><strong>Run errors</strong>${errors.map(error => `<div>${escape(error)}</div>`).join('')}</div>` : '',
    ...diagnostics.map((diagnostic, index) => diagnosticMarkup(diagnostic, String(index), expandedDiagnostics)),
    run.diagnosticsOmitted ? `<p class="hint diagnostics-omitted">${number(run.diagnosticsOmitted)} additional diagnostic entries were omitted to keep this run within its storage limits. Use the run ID to find service logs.</p>` : ''
  ].join('');
  const expandedRules = [...$('run-targets').querySelectorAll('details')].map(element => element.open);
  $('run-target-heading').textContent = direct ? 'Gateway processing' : 'Rule assignments';
  $('run-target-description').textContent = direct ? 'Pipelines and rules are evaluated by the gateway.' : 'Exact counts for this frozen selection.';
  $('run-target-table').hidden = direct;
  $('direct-run-note').hidden = !direct;
  $('direct-run-limits').textContent = `Overall minimum: ${number(run.minOverlays)} overlays · Maximum: ${number(run.maxOverlays)} overlays`;
  $('items-description').textContent = direct ? 'Overlay publication outcomes. Pipeline matching happens at the gateway.' : 'Assigned targets and publication outcomes.';
  $('items-target-heading').textContent = direct ? 'Processing' : 'Assigned rules';
  $('run-targets').innerHTML = run.targets.map((target, i) => `<tr><td>${escape(target.ruleName)}<span class="cell-sub">${escape(target.pipelineId)}${target.isActive ? '' : ' · inactive snapshot'}</span><details class="frozen-rule" ${expandedRules[i] ? 'open' : ''}><summary>Frozen conditions</summary><div class="cell-sub">${escape(ruleDescription(target.rule))}</div><div class="cell-sub mono">${escape(target.ruleId)}</div>${target.rule.match?.locationWkt ? `<div class="cell-sub mono">${escape(target.rule.match.locationWkt)}</div>` : ''}</details></td><td>${number(target.minOverlays)} / ${number(target.maxOverlays)}</td><td>${number(target.selected)}</td><td>${number(target.confirmed)}</td><td>${number(target.failed)}</td><td>${number(target.uncertain)}</td><td>${target.shortfall ? `<span class="tag warn">${number(target.shortfall)}</span>` : '—'}</td></tr>`).join('');
  if (!state.publicationDirty) {
    $('run-batch-size').value = run.publication.batchSize;
    $('run-delay-ms').value = run.publication.delayMs;
  }
  const canUpdate = !run.deletionRequested && ['Preparing', 'Ready', 'Publishing'].includes(run.state);
  $('update-publication').disabled = !canUpdate;
  $('run-batch-size').disabled = !canUpdate;
  $('run-delay-ms').disabled = !canUpdate;
  syncPublicationPresets('run-', !canUpdate);
  $('publication-status').textContent = run.publicationChangePending
    ? `Pending next batch · active: ${run.activePublication.batchSize} overlays / ${run.activePublication.delayMs} ms`
    : `Active: ${run.activePublication.batchSize} overlays / ${run.activePublication.delayMs} ms`;
  $('provider-queries').textContent = run.queries.length ? JSON.stringify(run.queries, null, 2)
    : run.sourceMode === 'OverlayIds' ? 'Overlays fetched by ID. No filter query was used.' : 'No provider queries recorded.';
}

let itemsVersion = 0;
async function loadItems() {
  const id = state.runId;
  if (!id) return;
  const version = ++itemsVersion;
  try {
    const page = await api.items(id, state.itemsFrom, state.itemsSize, state.itemsOutcome);
    if (id !== state.runId || version !== itemsVersion) return;
    $('run-items').innerHTML = page.items.length ? page.items.map(item => `<tr><td><span class="mono">${escape(item.overlayId)}</span><span class="cell-sub">${escape(item.sensorName)}${item.sensorType ? ` · ${escape(item.sensorType)}` : ''}</span></td><td>${escape(formatTime(item.photoTime))}</td><td>${directRun(state.run) ? '<span class="cell-sub">Gateway rules</span>' : item.targets.map(target => `<div>${escape(target.pipelineId)}<span class="cell-sub mono">${escape(target.ruleId)}</span></div>`).join('')}</td><td>${badge(item.outcome)}${item.error ? `<span class="item-error">${escape(item.error)}</span>` : ''}</td></tr>`).join('') : '<tr><td colspan="4"><div class="run-empty">No selected overlays for this view.</div></td></tr>';
    $('items-count').textContent = page.total ? `${number(page.from + 1)}–${number(page.from + page.items.length)} of ${number(page.total)} overlays` : '0 overlays';
    $('items-prev').disabled = state.itemsFrom === 0;
    $('items-next').disabled = page.from + page.items.length >= page.total;
  } catch (error) {
    if (id !== state.runId || version !== itemsVersion) return;
    $('run-items').innerHTML = `<tr><td colspan="4"><div class="inline-error">${errorMarkup(error)}</div></td></tr>`;
    $('items-prev').disabled = true; $('items-next').disabled = true;
  }
}

async function runAction(action) {
  const id = state.runId;
  if (!id || !state.run || state.run.deletionRequested || pendingRunActions.has(id)) return;
  if (action === 'start' && (state.run.state !== 'Ready' || state.run.counters.selected === 0)) return;
  if (action === 'cancel' && terminal(state.run.state)) return;
  pendingRunActions.set(id, action);
  renderRunActions(state.run);
  hideError('run-error');
  try {
    const run = await api[action](id);
    if (id !== state.runId) return;
    state.run = run; renderRun(run);
    toast(action === 'start' ? 'Publication started.' : 'Cancellation requested. Already queued messages remain queued.');
    await refreshRun();
  } catch (error) { if (id === state.runId) showError('run-error', error); }
  finally {
    pendingRunActions.delete(id);
    if (id === state.runId && state.run) renderRunActions(state.run);
  }
}

async function rerun() {
  const id = state.runId;
  if (!id || !state.run || state.run.deletionRequested || state.rerunPending || pendingRunActions.has(id) || ['Preparing', 'Publishing'].includes(state.run.state)) return;
  state.rerunPending = true;
  $('rerun-run').disabled = true;
  $('rerun-run').textContent = 'Preparing…';
  hideError('run-error');
  try {
    const run = await api.rerun(id);
    if (id !== state.runId) return;
    navigate(`/ui/auto-run/runs/${encodeURIComponent(run.id)}`);
    toast('New preview created from the original configuration and saved publication settings. Review it before starting.');
  } catch (error) { if (id === state.runId) showError('run-error', error); }
  finally {
    state.rerunPending = false;
    if ($('rerun-run')) {
      $('rerun-run').disabled = false;
      $('rerun-run').textContent = 'Rerun';
    }
  }
}

async function updatePublication(event) {
  event.preventDefault();
  const id = state.runId;
  hideError('run-error');
  // An older poll must not overwrite the settings returned by this mutation.
  clearTimeout(state.timer);
  state.refreshVersion++;
  const button = $('update-publication');
  button.disabled = true;
  try {
    const run = await api.publication(id, {
      batchSize: Number($('run-batch-size').value), delayMs: Number($('run-delay-ms').value)
    });
    if (id !== state.runId) return;
    state.publicationDirty = false; state.run = run; renderRun(run);
    toast(run.publicationChangePending ? 'Settings saved. They apply at the next batch boundary.' : 'Publication settings updated.');
  } catch (error) { if (id === state.runId) showError('run-error', error); }
  finally {
    if (id === state.runId) {
      button.disabled = terminal(state.run?.state);
      if (!terminal(state.run?.state)) state.timer = setTimeout(() => refreshRun(), 2000);
    }
  }
}

function renderProviderProgress(run) {
  const progress = run.state === 'Preparing' ? run.providerProgress : null;
  const element = $('provider-progress');
  element.hidden = !progress;
  if (!progress) return;
  const context = [progress.operation,
    progress.queryIndex != null ? `query ${number(progress.queryIndex)}${progress.queryCount ? ` of ${number(progress.queryCount)}` : ''}` : '',
    progress.page != null ? `page ${number(progress.page)}` : '',
    progress.attempt ? `attempt ${number(progress.attempt)} of ${number(progress.maxAttempts || progress.attempt)}` : '',
    progress.lastStatusCode != null ? `last response HTTP ${progress.lastStatusCode}` : ''].filter(Boolean).join(' · ');
  const remaining = progress.nextAttemptAt ? Math.max(0, Math.ceil((new Date(progress.nextAttemptAt).getTime() - Date.now()) / 1000)) : null;
  const waiting = progress.phase === 'retryWait'
    ? `Waiting to retry Elastic Provider${remaining !== null && Number.isFinite(remaining) ? remaining > 0 ? ` in ${remaining} s` : '; retry is due' : ''}.`
    : progress.phase === 'requestDelay'
      ? `Waiting between provider requests${remaining !== null && Number.isFinite(remaining) && remaining > 0 ? ` · next request in ${remaining} s` : ''}.`
      : 'Waiting for Elastic Provider to respond.';
  element.textContent = `${waiting} ${context}. Already scanned ${number(run.counters.scanned)} records.`;
}

function contextMarkup(entries) {
  const values = entries.filter(([, value]) => value !== null && value !== undefined && value !== '');
  return values.length ? `<dl class="diagnostic-context">${values.map(([label, value]) => `<div><dt>${escape(label)}</dt><dd>${escape(String(value).slice(0, 1000))}</dd></div>`).join('')}</dl>` : '';
}

function responseBodyMarkup(value, truncated, unavailableReason) {
  if (value == null || value === '') return `<p class="hint">${escape(unavailableReason ? `Response body unavailable: ${unavailableReason}` : 'No response body was returned.')}</p>`;
  let text = String(value);
  if (!truncated) {
    try { text = JSON.stringify(JSON.parse(text), null, 2); } catch { /* Keep plain text and partial JSON readable. */ }
  }
  const shortened = truncated || text.length > 8192;
  return `<pre class="diagnostic-body mono" tabindex="0" aria-label="Response body">${escape(text.slice(0, 8192))}</pre>${shortened ? '<p class="hint diagnostic-truncated">Response excerpt shortened; the full response is not retained here.</p>' : ''}${unavailableReason ? `<p class="hint">${escape(unavailableReason)}</p>` : ''}`;
}

function diagnosticMarkup(diagnostic, index = '', expanded = new Set(), messageShown = false) {
  const upstream = diagnostic.upstream;
  const key = `${diagnostic.timestamp ?? ''}|${diagnostic.code ?? ''}|${diagnostic.overlayId ?? ''}|${index}`;
  const severity = String(diagnostic.severity ?? 'Error').toLowerCase();
  const style = severity === 'warning' ? 'warning' : ['information', 'info', 'success'].includes(severity) ? 'success' : 'error';
  const context = contextMarkup([
    ['Stage', diagnostic.stage], ['Code', diagnostic.code], ['Recorded', diagnostic.timestamp ? formatTime(diagnostic.timestamp) : null],
    ['Operation', upstream?.operation], ['Method', upstream?.method], ['HTTP status', upstream?.statusCode],
    ['Attempts', upstream?.attempts], ['Elapsed', upstream?.elapsedMs != null ? `${number(Math.round(upstream.elapsedMs))} ms` : null],
    ['Query', diagnostic.queryIndex], ['Page', diagnostic.page], ['Overlay ID', diagnostic.overlayId],
    ['Pipeline', diagnostic.pipelineId], ['Rule ID', diagnostic.ruleId], ['Trace ID', diagnostic.traceId],
    ['Content type', upstream?.contentType], ['Response bytes read', upstream?.bytesRead]
  ]);
  return `<div class="run-diagnostic${messageShown ? '' : ` notice ${style}`}">${messageShown ? '' : `<p class="diagnostic-message">${escape(diagnostic.message)}</p>`}<details class="diagnostic-details" data-diagnostic-key="${escape(key)}" ${expanded.has(key) ? 'open' : ''}><summary>${upstream ? 'Provider response & details' : 'Diagnostic details'}${upstream?.statusCode != null ? ` · HTTP ${escape(upstream.statusCode)}` : ''}</summary>${context}${upstream ? responseBodyMarkup(upstream.bodyExcerpt, upstream.bodyTruncated, upstream.bodyUnavailableReason) : ''}</details></div>`;
}

function errorMarkup(error, depth = 0) {
  const fields = error.validationErrors;
  const context = contextMarkup([
    ['Request', error.method && error.path ? `${error.method} ${error.path.split('?')[0]}` : null],
    ['HTTP status', error.status > 0 ? error.status : null], ['Code', error.code], ['Trace ID', error.traceId]
  ]);
  const validation = fields ? `<ul class="error-fields">${Object.entries(fields).slice(0, 100).map(([field, messages]) =>
    `<li><strong>${escape(field || 'Request')}</strong>: ${escape([].concat(messages).join(' ').slice(0, 1000))}</li>`).join('')}</ul>` : '';
  return `<p class="error-message">${escape(String(error.message || error).slice(0, 4000))}</p>${validation}${context}${error.diagnostic ? diagnosticMarkup(error.diagnostic, 'request', new Set(), error.diagnostic.message === error.message) : ''}${error.responseBody != null ? `<details class="diagnostic-details"><summary>Rules API response</summary>${responseBodyMarkup(error.responseBody, error.responseBodyTruncated)}</details>` : ''}${depth < 1 && error.causes ? error.causes.slice(0, 100).map(cause => `<div class="error-cause">${errorMarkup(cause, depth + 1)}</div>`).join('') : ''}`;
}

function showError(id, error, source = 'action') {
  const element = $(id);
  element.classList.add(id === 'global-notice' ? 'notice' : 'error');
  if (id === 'global-notice') element.classList.add('error');
  element.innerHTML = errorMarkup(error);
  element.dataset.source = source;
  element.hidden = false;
}
function hideError(id) { $(id).hidden = true; }
let toastTimer;
function toast(message) {
  $('toast').textContent = message; $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, 5000);
}
