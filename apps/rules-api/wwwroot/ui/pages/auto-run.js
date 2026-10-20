import { api } from '../api.js';
import { navigate } from '../router.js';

const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const number = value => Number(value || 0).toLocaleString();
const splitValues = value => [...new Set(value.split(/[\n,]+/).map(v => v.trim()).filter(Boolean))];
const terminal = state => ['Completed', 'CompletedWithErrors', 'Failed', 'Cancelled', 'Expired'].includes(state);
const localZone = Intl.DateTimeFormat().resolvedOptions().timeZone;
const state = {
  initialized: false, options: null, targets: [], rules: [], rulesFrom: 0, rulesSize: 10,
  pipelines: [], runId: null, run: null, timer: null, itemsFrom: 0, itemsSize: 20,
  itemsOutcome: '', publicationDirty: false, refreshVersion: 0, rulesVersion: 0
};
const $ = id => document.getElementById(id);
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
  if (!state.initialized || !$('configuration')) {
    state.initialized = true;
    $('app').innerHTML = `<div id="global-notice" role="alert" hidden></div><div id="configuration">${configurationMarkup()}</div><div id="run-detail" hidden></div>`;
    attachConfiguration();
    loadSetup();
  }
  clearTimeout(state.timer);
  state.refreshVersion++;
  state.runId = id;
  $('configuration').hidden = Boolean(id);
  $('run-detail').hidden = !id;
  if (!id) {
    document.title = 'Auto-run · Rules Console';
    loadHistory();
    return;
  }
  state.run = null;
  state.itemsFrom = 0;
  state.itemsOutcome = '';
  state.publicationDirty = false;
  $('run-detail').innerHTML = detailMarkup(id);
  attachDetail();
  document.title = 'Run details · Rules Console';
  await refreshRun(true);
}

function configurationMarkup() {
  return `<div class="page-heading"><div><div class="eyebrow">HISTORICAL PROCESSING</div><h1>Give historical data a new run.</h1><p>Find overlays, match saved rules, and review exactly what will be sent to the Unified Gateway.</p></div><div class="heading-mark" aria-hidden="true">↗</div></div>
  <div class="configure-layout"><form id="preview-form">
    <section class="panel">${panelHeader('01', 'Choose your source', 'Start with saved rules, custom filters, or specific overlays.')}<div class="panel-body">
      <div class="source-modes">
        ${[['Rules', '⌘', 'From rules', 'Search using rule conditions'], ['Filters', '≋', 'From filters', 'Define your own search'], ['OverlayIds', '▦', 'Overlay IDs', 'Use a specific collection']].map(([value, icon, title, desc], i) =>
          `<label class="source-option"><input type="radio" name="source-mode" value="${value}" ${i === 0 ? 'checked' : ''}><div class="source-symbol" aria-hidden="true">${icon}</div><strong>${title}</strong><small>${desc}</small></label>`).join('')}
      </div>
      <p id="source-description" class="source-description">Search conditions are derived from the rules you select below. Additional filters can narrow the search.</p>
      <div id="overlay-ids-field" hidden><div class="divider"></div><label class="field" for="overlay-ids"><span>Overlay IDs</span><textarea id="overlay-ids" placeholder="overlay-001&#10;overlay-002" spellcheck="false"></textarea><small>Separate IDs with commas or new lines. Duplicate IDs are counted once.</small></label></div>
      <details id="filter-details" class="filter-details"><summary>Overlay filters <span id="filters-description" class="muted">Optional for rule-derived searches</span></summary><div>
        <div class="field-row">${field('sensor-names', 'Sensor names', 'e.g. SENSOR_A, SENSOR_B')}${field('sensor-types', 'Sensor types', 'Comma-separated values')}</div>
        <div class="field-row">${field('grid-types', 'Grid types', 'Comma-separated values')}${field('sortie-ids', 'Sortie IDs', 'Comma-separated values')}</div>
        <div class="field-row"><label class="field" for="registration-quality"><span>Registration quality</span><select id="registration-quality"><option value="">Any quality</option><option value="Accurate">Accurate</option><option value="Sensor">Sensor</option><option value="Accurate,Sensor">Accurate or Sensor</option></select></label><label class="field" for="is-realtime"><span>Realtime overlay</span><select id="is-realtime"><option value="">Any</option><option value="true">Yes</option><option value="false">No</option></select></label></div>
        <div class="field-row">${field('resolution-min', 'Minimum resolution', 'No lower bound', 'number', 'min="0" step="any"')}${field('resolution-max', 'Maximum resolution', 'No upper bound', 'number', 'min="0" step="any"')}</div>
        <label class="field" for="area-wkt"><span>Search area · WKT</span><textarea id="area-wkt" class="mono" placeholder="POLYGON ((...)) or MULTIPOLYGON (((...)))" spellcheck="false"></textarea><small>Selects intersecting overlays. The rule’s geometry still determines the dispatched ROI.</small></label>
        <div class="filter-subheading">Time ranges</div>
        <div class="field-row"><label class="field" for="input-timezone"><span>Times entered in</span><select id="input-timezone"><option value="local">${escape(localZone)} · browser timezone</option><option value="utc">UTC</option></select></label><div class="field"><span>Time handling</span><small>Dates are converted to explicit UTC timestamps before submission. Both boundaries are optional.</small></div></div>
        <div class="field-row">${field('created-from', 'Created from', '', 'datetime-local')}${field('created-to', 'Created to', '', 'datetime-local')}</div>
        <div class="field-row">${field('photo-from', 'Photo taken from', '', 'datetime-local')}${field('photo-to', 'Photo taken to', '', 'datetime-local')}</div>
      </div></details>
    </div></section>
    <section class="panel">${panelHeader('02', 'Select pipeline rules', 'Each selected rule gets its own overlay quota.')}<div class="panel-body">
      <div class="toolbar"><label class="field" for="pipeline-select"><span>Pipeline</span><select id="pipeline-select"><option value="">Loading pipelines…</option></select></label><button type="button" class="button ghost" id="load-rules">Browse rules <span aria-hidden="true">↓</span></button></div>
      <div id="rule-browser" class="rule-browser" hidden><div class="browser-header"><span>AVAILABLE RULES · active and inactive</span><span id="rule-page-label"></span></div><div id="rule-list" class="browser-list"></div><div class="browser-footer"><div class="pagination"><button type="button" class="button compact ghost" id="rules-prev" aria-label="Previous rules page">←</button><button type="button" class="button compact ghost" id="rules-next" aria-label="Next rules page">→</button></div><button type="button" class="button compact" id="add-rules">Add selected rules</button></div></div>
      <details class="paste-rules"><summary>Already have rule IDs?</summary><div class="toolbar"><label class="field" for="pasted-rule-ids"><span>Rule IDs in the selected pipeline</span><textarea id="pasted-rule-ids" rows="2" placeholder="Paste IDs separated by commas or new lines"></textarea></label><button type="button" class="button" id="add-pasted-rules">Add IDs</button></div></details>
      <div id="rule-error" class="inline-error" role="alert" hidden></div>
      <div id="selected-targets"></div>
      <p class="hint" style="margin-top:14px">Inactive rules can be selected. Historical runs ignore maximum photo age; other rule conditions still apply.</p>
    </div></section>
    <section class="panel">${panelHeader('03', 'Set limits & publication', 'Preview first. Publication starts only when you choose Start.')}<div class="panel-body">
      <div class="field-row">${field('overall-min', 'Overall minimum overlays', '0', 'number', 'min="0" max="100000" value="0" step="1"')}${field('overall-max', 'Overall maximum overlays', 'Use the combined rule maximum', 'number', 'min="1" max="100000" step="1"')}</div>
      <p class="hint">Shared overlays count once overall and once for each assigned rule. Minimum shortfalls are reported, and available overlays can still run.</p>
      <div class="divider"></div>
      ${publicationPresets()}
      <div class="field-row">${field('batch-size', 'Overlays per batch', '', 'number', 'min="1" max="100" value="10" required step="1"')}${field('delay-ms', 'Delay between batches · ms', '', 'number', 'min="0" max="60000" value="1000" required step="1"')}</div>
      <p class="hint">Publication settings can also be changed while a run is publishing. Updates take effect at a batch boundary.</p>
    </div></section>
    <div id="form-error" class="notice error" role="alert" hidden></div>
    <div class="submit-bar"><div><strong>Review before you run.</strong><p>The preview freezes selected overlays and rule definitions.</p></div><button id="prepare-button" class="button primary" type="submit" disabled>Prepare preview <span aria-hidden="true">↗</span></button></div>
  </form>
  <aside class="configuration-aside">
    <section class="aside-card"><div class="eyebrow">HOW IT WORKS</div><ol class="process-steps"><li>Find & match<p>Fetch historical overlays and evaluate your selected rules.</p></li><li>Review the selection<p>Inspect overlaps, quotas, and any shortfalls before starting.</p></li><li>Send to the gateway<p>Track confirmed submissions and inspect failed or uncertain items.</p></li></ol></section>
    <section class="aside-card"><h3>Workspace limits</h3><div id="server-limits"><p class="hint" style="margin-top:12px">Loading server options…</p></div><p>Previews and jobs live in this instance’s memory. A restart clears them.</p></section>
    <section class="aside-card history-card"><div class="history-head"><h3>Recent runs</h3><button class="button compact ghost" type="button" id="refresh-history" aria-label="Refresh recent runs">↻</button></div><div id="history-list" class="history-list"><p class="hint">Loading recent runs…</p></div></section>
  </aside></div>`;
}

function attachConfiguration() {
  $('preview-form').addEventListener('submit', submitPreview);
  attachPublicationPresets();
  document.querySelectorAll('[name="source-mode"]').forEach(radio => radio.addEventListener('change', updateSource));
  $('pipeline-select').addEventListener('change', () => { state.rulesFrom = 0; loadRules(); });
  $('load-rules').addEventListener('click', () => { state.rulesFrom = 0; loadRules(); });
  $('rules-prev').addEventListener('click', () => { state.rulesFrom = Math.max(0, state.rulesFrom - state.rulesSize); loadRules(); });
  $('rules-next').addEventListener('click', () => { state.rulesFrom += state.rulesSize; loadRules(); });
  $('add-rules').addEventListener('click', () => {
    if (!$('pipeline-select').value) return showError('rule-error', new Error('Select a pipeline first.'));
    const selected = [...document.querySelectorAll('[name="rule-choice"]:checked')].map(input => state.rules[Number(input.value)]);
    try { selected.forEach(rule => addTarget($('pipeline-select').value, rule)); renderTargets(); renderRuleList(); } catch (error) { showError('rule-error', error); }
  });
  $('add-pasted-rules').addEventListener('click', addPastedRules);
  $('refresh-history').addEventListener('click', loadHistory);
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
}

async function loadSetup() {
  const [options, pipelines] = await Promise.allSettled([api.options(), api.pipelines()]);
  if (options.status === 'fulfilled') {
    state.options = options.value;
    const { limits, defaults } = options.value;
    $('batch-size').value = defaults.publication.batchSize;
    $('delay-ms').value = defaults.publication.delayMs;
    syncPublicationPresets();
    for (const [id, range] of [['batch-size', limits.batchSize], ['delay-ms', limits.delayMs]]) {
      $(id).min = range.min; $(id).max = range.max;
    }
    $('overall-max').max = limits.maxSelectedOverlays;
    $('overall-min').max = limits.maxSelectedOverlays;
    $('server-limits').innerHTML = [
      ['Selected overlays', number(limits.maxSelectedOverlays)], ['Records scanned', number(limits.maxScannedRecords)],
      ['Ready preview', `${limits.readyLifetimeMinutes} min`], ['Active operation', '1 at a time']
    ].map(([key, value]) => `<div class="limit-line"><span>${key}</span><strong>${value}</strong></div>`).join('');
    $('connection-state').textContent = options.value.enabled ? 'API connected' : 'Auto-run disabled';
    if (!options.value.enabled) showError('global-notice', new Error('Auto-run is disabled on this instance. Enable it in the service configuration to prepare runs.'));
  } else {
    $('connection-state').textContent = 'API unavailable';
    showError('global-notice', options.reason);
  }
  if (pipelines.status === 'fulfilled') {
    state.pipelines = pipelines.value;
    $('pipeline-select').innerHTML = '<option value="">Select a pipeline</option>' + pipelines.value.map(p =>
      `<option value="${escape(p.pipelineId)}" ${!p.enabled ? 'disabled' : ''}>${escape(p.pipelineId)}${p.enabled ? '' : ' · disabled'}</option>`).join('');
  } else {
    $('pipeline-select').innerHTML = '<option value="">Could not load pipelines</option>';
    showError('rule-error', pipelines.reason);
  }
  $('prepare-button').disabled = !state.options?.enabled || pipelines.status !== 'fulfilled';
}

function updateSource() {
  const mode = document.querySelector('[name="source-mode"]:checked').value;
  $('overlay-ids-field').hidden = mode !== 'OverlayIds';
  $('filter-details').hidden = mode === 'OverlayIds';
  $('filter-details').open = mode === 'Filters';
  $('filters-description').textContent = mode === 'Filters' ? 'Empty filters search all overlays' : 'Optional for rule-derived searches';
  $('source-description').textContent = {
    Rules: 'Search conditions are derived from the rules you select below. Additional filters can narrow the search.',
    Filters: 'Find overlays using the filters below, then match them against your selected pipeline rules.',
    OverlayIds: 'Fetch these overlays by ID, then match them against your selected pipeline rules.'
  }[mode];
}

function ruleDescription(rule) {
  const match = rule.match || {};
  const parts = [];
  if (rule.matchAll) parts.push('Matches all overlays');
  if (match.sensors?.length) parts.push(match.sensors.map(s => `${s.name}${s.registrationQualities?.length ? ` · ${s.registrationQualities.join('/')}` : ''}${s.gridTypes?.length ? ` · ${s.gridTypes.join('/')}` : ''}`).join('; '));
  if (match.resolution) parts.push(`Resolution ${match.resolution.min ?? 'any'}–${match.resolution.max ?? 'any'}`);
  if (match.locationWkt) parts.push('Geographic area');
  if (match.modeSAR?.length) parts.push(`SAR: ${match.modeSAR.join(', ')}`);
  if (match.maxPhotoAgeDays) parts.push(`${match.maxPhotoAgeDays}-day age limit ignored for historical runs`);
  return parts.join(' · ') || 'No additional match constraints';
}

async function loadRules() {
  const pipeline = $('pipeline-select').value;
  const version = ++state.rulesVersion;
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
    const rules = await api.rules(pipeline, state.rulesFrom, state.rulesSize);
    if (version !== state.rulesVersion) return;
    state.rules = rules;
    renderRuleList();
  } catch (error) {
    if (version !== state.rulesVersion) return;
    state.rules = [];
    $('rule-list').innerHTML = '<div class="run-empty">Rules could not be loaded. Try Browse rules again.</div>';
    showError('rule-error', error);
  }
}

function renderRuleList() {
  const pipeline = $('pipeline-select').value;
  $('rule-page-label').textContent = state.rules.length ? `${state.rulesFrom + 1}–${state.rulesFrom + state.rules.length}` : 'No rules';
  $('rule-list').innerHTML = state.rules.length ? state.rules.map((rule, i) => {
    const exists = state.targets.some(target => target.pipelineId === pipeline && target.ruleId === rule._id);
    return `<label class="rule-choice"><input type="checkbox" name="rule-choice" value="${i}" ${exists ? 'disabled' : ''}><div><div class="rule-choice-name">${escape(rule.ruleName)}${exists ? ' · added' : ''}</div><div class="rule-choice-meta">${escape(ruleDescription(rule))}</div><div class="cell-sub mono">${escape(rule._id)}</div></div>${badge(rule.isActive ? 'Active' : 'Inactive')}</label>`;
  }).join('') : '<div class="run-empty">No rules on this page.</div>';
  $('rules-prev').disabled = state.rulesFrom === 0;
  $('rules-next').disabled = state.rules.length < state.rulesSize;
  $('add-rules').disabled = !state.rules.length;
}

function addTarget(pipelineId, rule) {
  if (state.targets.some(target => target.pipelineId === pipelineId && target.ruleId === rule._id)) return;
  if (state.targets.length >= (state.options?.limits.maxTargets ?? 100)) throw new Error(`A preview supports at most ${state.options?.limits.maxTargets ?? 100} rule targets.`);
  state.targets.push({ pipelineId, ruleId: rule._id, minOverlays: '0', maxOverlays: '100', rule });
}

async function addPastedRules() {
  hideError('rule-error');
  const pipeline = $('pipeline-select').value;
  const ids = splitValues($('pasted-rule-ids').value);
  if (!pipeline || !ids.length) return showError('rule-error', new Error('Select a pipeline and enter at least one rule ID.'));
  if (ids.length > (state.options?.limits.maxTargets ?? 100)) return showError('rule-error', new Error('Too many rule IDs for one preview.'));
  const button = $('add-pasted-rules');
  button.disabled = true;
  try {
    const errors = [];
    // Limit lookup concurrency and show individual failures without losing successful selections.
    for (let i = 0; i < ids.length; i += 5) {
      const results = await Promise.allSettled(ids.slice(i, i + 5).map(id => api.rule(pipeline, id)));
      results.forEach((result, j) => {
        if (result.status === 'fulfilled') addTarget(pipeline, result.value);
        else errors.push(`${ids[i + j]}: ${result.reason.message}`);
      });
    }
    renderTargets(); renderRuleList();
    if (errors.length) showError('rule-error', new Error(errors.join(' ')));
    else $('pasted-rule-ids').value = '';
  } catch (error) { renderTargets(); showError('rule-error', error); }
  finally { button.disabled = false; }
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
  const utc = $('input-timezone').value === 'utc';
  const date = new Date(utc ? `${value}Z` : value);
  if (!Number.isFinite(date.getTime())) throw new Error('Enter a valid date and time.');
  if (!utc) {
    const sameLocal = candidate => {
      const pad = n => String(n).padStart(2, '0');
      return `${candidate.getFullYear()}-${pad(candidate.getMonth() + 1)}-${pad(candidate.getDate())}T${pad(candidate.getHours())}:${pad(candidate.getMinutes())}` === value.slice(0, 16);
    };
    if (!sameLocal(date)) throw new Error('This local time does not exist because of a daylight-saving change. Choose another time or enter it in UTC.');
    if ([-120, -60, -30, 30, 60, 120].some(minutes => sameLocal(new Date(date.getTime() + minutes * 60000))))
      throw new Error('This local time is ambiguous because of a daylight-saving change. Select UTC and enter the intended instant.');
  }
  return date.toISOString();
}

function collectFilters() {
  const result = {};
  for (const [id, key] of [['sensor-names', 'sensorNames'], ['sensor-types', 'sensorTypes'], ['grid-types', 'gridTypes'], ['sortie-ids', 'sortieIds']]) {
    const values = splitValues($(id).value); if (values.length) result[key] = values;
  }
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
  hideError('form-error');
  const button = $('prepare-button');
  try {
    if (!state.targets.length) throw new Error('Select at least one pipeline rule before preparing a preview.');
    const targets = state.targets.map(target => {
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
    button.disabled = true;
    button.textContent = 'Preparing…';
    const run = await api.preview({ source, targets, minOverlays: min, maxOverlays: max,
      publication: { batchSize: Number($('batch-size').value), delayMs: Number($('delay-ms').value) } });
    navigate(`/ui/auto-run/runs/${encodeURIComponent(run.id)}`);
  } catch (error) {
    showError('form-error', error);
    $('form-error').scrollIntoView({ block: 'nearest', behavior: 'smooth' });
  } finally {
    button.disabled = !state.options?.enabled;
    button.innerHTML = 'Prepare preview <span aria-hidden="true">↗</span>';
  }
}

async function loadHistory() {
  try {
    const runs = await api.list();
    $('history-list').innerHTML = runs.length ? [...runs].sort((a, b) => b.createdAt.localeCompare(a.createdAt)).slice(0, 8).map(run =>
      `<a href="/ui/auto-run/runs/${encodeURIComponent(run.id)}" data-route class="history-item"><div class="history-top"><span class="history-id">${escape(run.id)}</span>${badge(run.state)}</div><div class="history-meta"><span>${number(run.counters.selected)} overlays</span><span>${escape(formatTime(run.createdAt))}</span></div></a>`).join('')
      : '<p class="hint">No retained runs yet. Your first preview will appear here.</p>';
  } catch (error) { $('history-list').innerHTML = `<p class="hint">${escape(error.message)}</p>`; }
}

function detailMarkup(id) {
  return `<a class="back-link" href="/ui/auto-run" data-route>← Back to configuration</a>
  <div class="page-heading run-heading"><div><div class="eyebrow">RUN WORKSPACE</div><h1>Review. Launch. Follow through.</h1><div class="run-id mono">${escape(id)}</div></div><div class="run-heading-actions"><span id="run-state">${badge('Loading')}</span><button id="refresh-run" type="button" class="button ghost" aria-label="Refresh run">↻</button><button id="cancel-run" type="button" class="button danger" hidden>Cancel</button><button id="start-run" type="button" class="button primary" hidden>Start run ↗</button></div></div>
  <div id="run-error" class="notice error" role="alert" hidden></div>
  <div id="run-notices" hidden></div>
  <div id="run-content" hidden>
    <div class="stat-grid">${[['selected', 'Selected', ''], ['confirmed', 'Confirmed', 'good'], ['failed', 'Failed', 'bad'], ['uncertain', 'Uncertain', 'warning'], ['remaining', 'Remaining', '']].map(([key, label, style]) =>
      `<div class="stat ${style}"><div class="stat-label">${label}</div><div class="stat-value" id="count-${key}">0</div></div>`).join('')}</div>
    <section class="panel"><div class="progress-block"><div class="progress-text"><span id="progress-label"></span><span id="progress-value" class="mono"></span></div><div id="run-progress" class="progress-track" role="progressbar" aria-label="Publication progress" aria-valuemin="0" aria-valuemax="100" aria-valuenow="0"><div id="progress-fill"></div></div><div class="run-meta" id="run-meta"></div></div></section>
    <div class="run-layout"><section class="panel"><div class="panel-header"><div><h2>Rule assignments</h2><p>Exact counts for this frozen selection.</p></div></div><div class="table-wrap"><table><thead><tr><th>Rule / pipeline</th><th>Min / max</th><th>Selected</th><th>Confirmed</th><th>Failed</th><th>Uncertain</th><th>Shortfall</th></tr></thead><tbody id="run-targets"></tbody></table></div></section>
    <section class="panel run-publication"><div class="panel-header"><div><h2>Publication settings</h2><p>Control the pace of this run.</p></div></div><form id="publication-form" class="panel-body">${publicationPresets('run-')}<div class="field-row">${field('run-batch-size', 'Batch size', '', 'number', 'min="1" max="100" required step="1"')}${field('run-delay-ms', 'Delay · ms', '', 'number', 'min="0" max="60000" required step="1"')}</div><button class="button full" id="update-publication" type="submit">Apply settings</button><p id="publication-status" class="publication-status" role="status"></p><p class="hint">Changes apply at a batch boundary. Confirmed means queued to the gateway, not processed downstream.</p></form></section></div>
    <section class="panel"><div class="panel-header items-toolbar"><div><h2>Selected overlays</h2><p>Assigned targets and publication outcomes.</p></div><label class="field" for="item-outcome"><span class="visually-hidden">Filter items by outcome</span><select id="item-outcome"><option value="">All outcomes</option>${['Pending', 'InFlight', 'Confirmed', 'Failed', 'Uncertain'].map(v => `<option>${v}</option>`).join('')}</select></label></div><div class="table-wrap"><table class="items-table"><thead><tr><th>Overlay / sensor</th><th>Photo time</th><th>Assigned rules</th><th>Outcome</th></tr></thead><tbody id="run-items"></tbody></table></div><div class="browser-footer"><span id="items-count" class="hint"></span><div class="pagination"><button id="items-prev" class="button compact ghost" type="button">← Previous</button><button id="items-next" class="button compact ghost" type="button">Next →</button></div></div></section>
    <details class="panel"><summary class="panel-body" style="cursor:pointer;font-size:12px">Provider queries · frozen search</summary><div class="panel-body" style="padding-top:0"><pre id="provider-queries" class="mono" style="white-space:pre-wrap;overflow-wrap:anywhere;font-size:11px;color:#a7bbc9"></pre></div></details>
  </div><div id="run-loading" class="loading"><span class="spinner"></span>Loading run…</div>`;
}

function attachDetail() {
  $('refresh-run').addEventListener('click', () => refreshRun(true));
  $('start-run').addEventListener('click', () => runAction('start'));
  $('cancel-run').addEventListener('click', () => runAction('cancel'));
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
    hideError('run-error');
    $('run-loading').hidden = true;
    $('run-content').hidden = false;
    renderRun(run);
    await loadItems();
    if (id === state.runId && version === state.refreshVersion && !terminal(run.state)) {
      state.timer = setTimeout(() => refreshRun(), run.state === 'Ready' ? 5000 : 2000);
    }
  } catch (error) {
    if (id !== state.runId || version !== state.refreshVersion) return;
    $('run-loading').hidden = true;
    showError('run-error', error.status === 404 ? new Error('This run is no longer available. It may have expired, or the service may have restarted. Return to configuration to prepare a new preview.') : error);
    if (error.status !== 404 && (!state.run || !terminal(state.run.state))) state.timer = setTimeout(() => refreshRun(), 5000);
  }
}

function renderRun(run) {
  $('run-state').innerHTML = badge(run.state);
  $('start-run').hidden = run.state !== 'Ready';
  $('cancel-run').hidden = terminal(run.state);
  for (const key of ['selected', 'confirmed', 'failed', 'uncertain', 'remaining']) $(`count-${key}`).textContent = number(run.counters[key]);
  const complete = run.counters.confirmed + run.counters.failed + run.counters.uncertain;
  const percent = run.counters.selected ? Math.min(100, Math.round(complete / run.counters.selected * 100)) : 0;
  $('progress-fill').style.width = `${percent}%`;
  $('run-progress').setAttribute('aria-valuenow', String(percent));
  $('progress-value').textContent = run.state === 'Preparing' ? `${number(run.counters.scanned)} scanned` : `${percent}%`;
  $('progress-label').textContent = {
    Preparing: 'Fetching and matching overlays…', Ready: 'Preview ready · review the selection, then start',
    Publishing: `Publishing · ${Number(run.confirmedPerSecond || 0).toFixed(1)} confirmed/s`,
    Completed: 'Publication complete · submissions confirmed by RabbitMQ',
    CompletedWithErrors: 'Publication finished · inspect failed and uncertain items',
    Failed: 'Run failed · inspect the errors below', Cancelled: 'Cancelled · queued messages remain queued',
    Expired: 'Preview expired · prepare a new run'
  }[run.state] || run.state;
  $('run-meta').innerHTML = [
    `Scanned ${number(run.counters.scanned)}`, `Observed matches ${number(run.counters.observedMatches)}`,
    `Duplicates ${number(run.counters.duplicates)}`, `Invalid ${number(run.counters.invalid)}`,
    `In flight ${number(run.counters.inFlight)}`, `Source ${run.sourceExhausted ? 'exhausted' : 'not exhausted'}`,
    `Created ${formatTime(run.createdAt)}`, ...(run.expiresAt ? [`Expires ${formatTime(run.expiresAt)}`] : [])
  ].map(value => `<span>${escape(value)}</span>`).join('');
  const warnings = [...run.warnings];
  if (run.shortfall > 0) warnings.unshift(`Overall minimum shortfall: ${number(run.shortfall)} overlays. The available selection can still run.`);
  if (run.stoppedReason && !run.sourceExhausted) warnings.push(`Search stopped: ${run.stoppedReason}. Observed counts are not a complete source total.`);
  $('run-notices').hidden = !warnings.length && !run.errors.length;
  $('run-notices').innerHTML = [
    warnings.length ? `<div class="notice warning"><strong>Selection & run notes</strong><ul class="message-list">${warnings.map(w => `<li>${escape(w)}</li>`).join('')}</ul></div>` : '',
    run.errors.length ? `<div class="notice error"><strong>Run errors</strong>${run.errors.map(error => `<div>${escape(error)}</div>`).join('')}</div>` : ''
  ].join('');
  const expandedRules = [...$('run-targets').querySelectorAll('details')].map(element => element.open);
  $('run-targets').innerHTML = run.targets.map((target, i) => `<tr><td>${escape(target.ruleName)}<span class="cell-sub">${escape(target.pipelineId)}${target.isActive ? '' : ' · inactive snapshot'}</span><details class="frozen-rule" ${expandedRules[i] ? 'open' : ''}><summary>Frozen conditions</summary><div class="cell-sub">${escape(ruleDescription(target.rule))}</div><div class="cell-sub mono">${escape(target.ruleId)}</div>${target.rule.match?.locationWkt ? `<div class="cell-sub mono">${escape(target.rule.match.locationWkt)}</div>` : ''}</details></td><td>${number(target.minOverlays)} / ${number(target.maxOverlays)}</td><td>${number(target.selected)}</td><td>${number(target.confirmed)}</td><td>${number(target.failed)}</td><td>${number(target.uncertain)}</td><td>${target.shortfall ? `<span class="tag warn">${number(target.shortfall)}</span>` : '—'}</td></tr>`).join('');
  if (!state.publicationDirty) {
    $('run-batch-size').value = run.publication.batchSize;
    $('run-delay-ms').value = run.publication.delayMs;
  }
  const canUpdate = ['Preparing', 'Ready', 'Publishing'].includes(run.state);
  $('update-publication').disabled = !canUpdate;
  $('run-batch-size').disabled = !canUpdate;
  $('run-delay-ms').disabled = !canUpdate;
  syncPublicationPresets('run-', !canUpdate);
  $('publication-status').textContent = run.publicationChangePending
    ? `Pending next batch · active: ${run.activePublication.batchSize} overlays / ${run.activePublication.delayMs} ms`
    : `Active: ${run.activePublication.batchSize} overlays / ${run.activePublication.delayMs} ms`;
  $('provider-queries').textContent = run.queries.length ? JSON.stringify(run.queries, null, 2) : 'No provider queries recorded.';
}

let itemsVersion = 0;
async function loadItems() {
  const id = state.runId;
  if (!id) return;
  const version = ++itemsVersion;
  try {
    const page = await api.items(id, state.itemsFrom, state.itemsSize, state.itemsOutcome);
    if (id !== state.runId || version !== itemsVersion) return;
    $('run-items').innerHTML = page.items.length ? page.items.map(item => `<tr><td><span class="mono">${escape(item.overlayId)}</span><span class="cell-sub">${escape(item.sensorName)}</span></td><td>${escape(formatTime(item.photoTime))}</td><td>${item.targets.map(target => `<div>${escape(target.pipelineId)}<span class="cell-sub mono">${escape(target.ruleId)}</span></div>`).join('')}</td><td>${badge(item.outcome)}${item.error ? `<span class="item-error">${escape(item.error)}</span>` : ''}</td></tr>`).join('') : '<tr><td colspan="4"><div class="run-empty">No selected overlays for this view.</div></td></tr>';
    $('items-count').textContent = page.total ? `${number(page.from + 1)}–${number(page.from + page.items.length)} of ${number(page.total)} items` : '0 items';
    $('items-prev').disabled = state.itemsFrom === 0;
    $('items-next').disabled = page.from + page.items.length >= page.total;
  } catch (error) {
    if (id !== state.runId || version !== itemsVersion) return;
    $('run-items').innerHTML = `<tr><td colspan="4"><div class="inline-error">${escape(error.message)}</div></td></tr>`;
    $('items-prev').disabled = true; $('items-next').disabled = true;
  }
}

async function runAction(action) {
  const id = state.runId;
  if (!id) return;
  const button = $(`${action}-run`);
  button.disabled = true;
  hideError('run-error');
  try {
    const run = await api[action](id);
    if (id !== state.runId) return;
    state.run = run; renderRun(run);
    toast(action === 'start' ? 'Publication started.' : 'Cancellation requested. Already queued messages remain queued.');
    await refreshRun();
  } catch (error) { if (id === state.runId) showError('run-error', error); }
  finally { if (id === state.runId) button.disabled = false; }
}

async function updatePublication(event) {
  event.preventDefault();
  const id = state.runId;
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

function showError(id, error) {
  const element = $(id);
  element.classList.add(id === 'global-notice' ? 'notice' : 'error');
  if (id === 'global-notice') element.classList.add('error');
  element.textContent = error.message || String(error);
  element.hidden = false;
}
function hideError(id) { $(id).hidden = true; }
let toastTimer;
function toast(message) {
  $('toast').textContent = message; $('toast').hidden = false;
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => { $('toast').hidden = true; }, 5000);
}
