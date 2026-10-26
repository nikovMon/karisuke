# Rules API

`rules-api` manages rule configuration documents for the imaging pipeline. It exposes REST routes under `/rules` and `/pipelines`, stores documents in Elasticsearch, and returns JSON. Historical auto-run routes under `/auto-runs` prepare overlays from Elastic Provider and publish selected work to the Unified Gateway.

Swagger is available at `/swagger` when the app is running. Health is available at `/health`. The Flarules UI is available at `/ui/auto-run`; `/ui` redirects there. Its shared navigation also includes **Rules** (`/ui/rules`) and **Rules Simulate** (`/ui/rules-simulate`), which currently display **Coming Soon**, and API Reference.

## Runtime

Default local settings:

```json
{
  "Elasticsearch": {
    "Uri": "http://localhost:9200",
    "Index": "rules",
    "TimeoutSeconds": 30,
    "Username": "",
    "Password": "",
    "DefaultSearchSize": 100,
    "MaxSearchSize": 1000
  }
}
```

Set both credentials when Elasticsearch authentication is enabled. Prefer environment variables instead of storing secrets in the file:

```bash
Elasticsearch__Username=elastic
Elasticsearch__Password=your-secret
```

The client sends HTTP Basic Authentication whenever both values are configured. Supplying only one credential fails configuration validation during startup.

Run locally:

```bash
dotnet run --project apps/rules-api/ImagingPipeline.Rules.Api.csproj
```

Docker Compose exposes the service on host port `8080`.

### Pipeline catalog

The API loads the same pipeline catalog as the unified gateway: `pipelinecatalog.json` next to the app, or the file named by the `PipelineCatalogFile` setting. An invalid catalog stops startup. The API reads pipeline IDs and contracts from it, never transport settings. The catalog still has to validate in full, so RabbitMQ credentials only need placeholder values here.

## Historical auto-run

Auto-run has two explicit stages: prepare a preview, then start publication. Preparation fetches overlays through Elastic Provider and freezes their normalized payloads. When rules are selected, it also evaluates those saved rules, checks each pipeline's input contract, and freezes rule definitions and target assignments. Editing or deleting a selected rule after preparation does not change that run. Filters and Overlay IDs can instead submit overlays directly without selecting rules; the gateway then applies its current rules when it consumes the ordinary input messages.

The feature is disabled by default. Jobs and previews are held in memory on **one Rules API instance**. Only one job may prepare or publish at a time; unused ready previews can coexist within the retention and payload limits. Restarting the instance loses all jobs and previews. There is no scheduled execution, restart recovery, or coordination between API replicas.

Flarules uses the same API routes, supports all three source modes, browses saved rules, shows per-rule quotas and preview shortfalls, and polls run progress. Run links use `/ui/auto-run/runs/{id}`. Date inputs use the browser's local timezone automatically, without a timezone selector, and submit explicit UTC timestamps. The UI and API use the deployment's existing access boundary; this feature adds no login, roles, or per-user job isolation.

The rule browser searches within the selected pipeline across pages and supports an active/inactive status filter. **Select all matching rules** adds the rules matching those search/status filters across all pages; without filters it includes every rule, including inactive ones. Rules already selected retain their edited quotas, and targets from other pipelines remain selected. The operation adds nothing if fetching a page fails or the combined selection would exceed the 100-target limit; select fewer rules individually in that case. Selecting rules prepares the configuration only; it does not start a preview or publish messages.

`GET /pipelines/{pipelineId}/rules?search=thermal&isActive=true&from=0&size=10` supports literal, case-insensitive substring matching across `ruleName`, rule ID, description, sensor names, and sensor types. `search` is trimmed and limited to 200 characters; an omitted or blank search retains the existing list behavior. `from` skips matching rules, and `getNameOnly=true` supports the same search. Search symbols such as `*` are treated literally.

Existing rules indexes can disable indexing of match conditions, so text search scans projected source fields through an Elasticsearch point-in-time snapshot, keeping at most 500 projected records per scan page plus the requested result page. Only the selected result page's full rule documents are fetched, from that same snapshot. A request that scans 100,000 rules without finding the requested page or exhausting the source returns an explicit `422` scan-limit error. Each list request opens its own snapshot; edits between UI pages can affect subsequent results. Cancellation and completion close the snapshot. This search concerns saved rule metadata, independently of the Elastic Provider overlay searches.

In **Overlay IDs** mode, direct submission is the default: enter IDs, prepare a preview, then start. No pipeline, rules, or search filters are required. The optional selected-rule mode retains historical matching and per-rule quotas. Switching modes preserves the rule draft, but direct requests do not send hidden targets or filters. Direct previews remain available if the pipeline list cannot be loaded.

In **From filters**, the UI shows overlay filters and overall quotas without rule selection. It sends `targets: []` and prepares ordinary gateway input. Its default overall maximum is 100 overlays, capped by the server's configured maximum. Use **From rules** with optional filters when the preview should evaluate specific saved rules and freeze their assignments.

The optional filter forms in both modes omit grid types and sortie IDs. Existing API clients can still supply `source.filters.gridTypes` and `source.filters.sortieIds`; saved rules' grid-type conditions are still honored. The run page refreshes automatically while active; its redundant circular refresh control has been removed.

**Sensor types** is a checkbox multi-select populated from `provider.sensorTypeMappings` in the options response. With no selection, this filter does not restrict sensor type. The same selection is preserved when switching between Rules and Filters or returning from a run; Overlay IDs does not submit the hidden filters. Types are configured choices rather than free-text values. The run page shows the resolved sensor type beside each selected overlay and preserves the exact provider query in its frozen-query view.

**Open map**, beside Search area, opens the website configured in `AutoRun:Ui:MapUrl` in an iframe dialog. The default is blank, which disables the button and shows a configuration hint. Supply an absolute HTTP(S) URL without embedded credentials. The map loads only when opened and unloads on close. Copy WKT from the map and paste it into Search area; no cross-site geometry transfer protocol is assumed. The configured website must permit iframe embedding. An **Open map in new tab** link is available when embedding is blocked. The dialog supports keyboard navigation, Escape from its own controls, and focus return to the trigger; the embedded website controls its own keyboard events.

### Workflow and routes

| Method and route | Behavior |
| --- | --- |
| `GET /auto-runs/options` | Feature status, publication defaults, effective limits, `ui.mapUrl`, provider response limit, timeout, `to`, request/retry delays, retry count and sensor-type catalog, current `memory` measurements, and `singleInstance: true`. |
| `POST /auto-runs/preview` | Accepts a preview request and returns `202 Accepted`, a run summary, and its `Location` URL. No messages are published. |
| `GET /auto-runs` | Lists retained run summaries, newest first. |
| `GET /auto-runs/{id}` | Returns state, counters, target quotas, frozen rules, provider queries, warnings, errors, structured `diagnostics`, `diagnosticsOmitted`, current `providerProgress`, per-run `retainedBytes`, `deletionRequested`, and current process `memory` measurements. |
| `GET /auto-runs/{id}/items?from=0&size=50` | Pages through selected overlays and their assigned pipeline/rule pairs. `size` is 1–200; optional `outcome` filters by `Pending`, `InFlight`, `Confirmed`, `Failed`, or `Uncertain`. |
| `POST /auto-runs/{id}/start` | Starts a ready, unexpired preview. Repeating start for a run that has already started returns its current state without publishing it again. |
| `POST /auto-runs/{id}/rerun` | Prepares a new run from retained criteria with the latest publication settings. Returns `202 Accepted`, a new run summary, and its `Location` URL. No messages are published. |
| `PATCH /auto-runs/{id}/publication` | Changes batch size and delay during preparation, while ready, or during publication. |
| `POST /auto-runs/{id}/cancel` | Cancels preparation or publication, or discards a ready preview. Already queued messages are not recalled. |
| `DELETE /auto-runs/{id}` | Deletes a retained run. If work is still queued or executing, cancels future work and waits for outstanding work before releasing its data. |
| `DELETE /auto-runs` | Deletes all retained runs and requests cancellation/deletion of any active run. |

Poll the summary until `state` is `Ready`, inspect the selection and warnings, then call start. States are `Preparing`, `Ready`, `Publishing`, `Completed`, `CompletedWithErrors`, `Failed`, `Cancelled`, and `Expired`. Cancellation is cooperative; an active run may remain `Preparing` or `Publishing` until outstanding work settles.

**Rerun** uses the original source, filters, target IDs, and quotas with that run's latest requested batch size and delay. It fetches current overlays and re-resolves current saved rules, producing a fresh preview with a new run ID; it does not reuse the old frozen payloads or rule snapshots. Review the new preview and explicitly click **Start**. The rerun route takes no body, returns `409` if the original run is `Preparing` or `Publishing` or another job is active, and returns `404` if the original run is no longer retained, including after a restart.

Invalid requests return `400`; missing or forgotten runs return `404`; a busy worker, full retained-job budget, or invalid state transition returns `409`; preparing while disabled returns `503`. Inspect the job summary after acceptance rather than relying only on the initial HTTP response. A non-retryable provider failure changes the job to `Failed`. Exhausted HTTP 429/503 retries instead stop collection and produce a partial `Ready` preview from eligible candidates already collected, with `sourceExhausted: false` and `stoppedReason: "providerUnavailable"`; an empty selection cannot start.

### Recent runs and memory cleanup

Recent runs lists every retained job with its retained-data size, a **Delete** button, and a **Delete all** control above the list. `retainedBytes` counts the serialized request, frozen rules, queries, structured diagnostics, and still-retained overlay payloads charged to the payload budget. It is an estimate of retained data, not the job's full heap size; CLR objects and item metadata add overhead. Terminal jobs have already released their overlay payloads, so their remaining size is usually smaller. Diagnostics remain with the summary until deletion or expiry and their retained byte charge is released with the run.

Deletion returns `{ "deletedIds": [], "pendingIds": [], "releasedRetainedBytes": 0 }`, with HTTP `200` when all requested removals are immediate or `202` while any are pending. The released byte count covers removals completed during that request. Pending jobs expose `deletionRequested: true`; the UI polls until they disappear. Their settings, start, and rerun actions are disabled. Once removed, run and item URLs return `404`; saved rules are unaffected. Bulk deletion is safe to repeat when no jobs remain.

Deleting an active job stops future preparation/publication and releases data only after queued or in-flight work finishes. It does not recall confirmed messages or resolve uncertain delivery outcomes. Cleanup releases application references and payload-budget capacity; garbage collection controls when memory is reclaimed and the process working set may remain high. No forced garbage collection is performed. All users on this instance share the same retained-run collection.

### Preview requests

`Rules` requires 1–100 unique `(pipelineId, ruleId)` targets. `Filters` and `OverlayIds` accept omitted or empty `targets` for direct gateway submission. Existing API callers may continue to supply targets in `Filters` mode for local rule matching; the UI uses the direct form. When targets are supplied, IDs reference existing schema-version-2 rules from `/pipelines/{pipelineId}/rules`, and the pipeline must be enabled in the catalog. An explicitly selected inactive rule is allowed. Historical matching with selected targets ignores only `maxPhotoAgeDays`; sensor, registration quality, grid type, sensor type, resolution, geometry, and `modeSAR` conditions still apply. `sensorTypes` is also sent to the provider as a sensor-type filter.

`Rules` derives provider queries from the selected rules. Each sensor branch retains its own quality and grid conditions. The adapter finishes every page of one query before starting the next query, preserving that query's filter and sort body throughout its continuation chain. Searches are not interleaved because the provider does not support multiple active filter/sort searches. Identical queries and repeated overlay IDs are deduplicated. Optional `source.filters` narrows those searches:

```json
{
  "source": {
    "mode": "Rules",
    "filters": {
      "createdTime": {
        "from": "2026-01-01T00:00:00Z",
        "to": "2026-01-31T23:59:59Z"
      }
    }
  },
  "targets": [
    {
      "pipelineId": "FindAir",
      "ruleId": "replace-with-findair-rule-id",
      "minOverlays": 20,
      "maxOverlays": 100
    },
    {
      "pipelineId": "DeepWisdom",
      "ruleId": "replace-with-deepwisdom-rule-id",
      "minOverlays": 10,
      "maxOverlays": 50
    }
  ],
  "minOverlays": 30,
  "maxOverlays": 120,
  "publication": { "batchSize": 10, "delayMs": 1000 }
}
```

`Filters` searches with explicit overlay filters and rechecks those filters locally. With no targets, it prepares ordinary gateway input without local rule matching or per-pipeline contract checks. With targets, it also evaluates those selected rules locally, preserving the existing API behavior. Empty or omitted filters search the provider without additional conditions; scan and candidate limits still apply. This example shows every supported filter for direct submission; replace the illustrative field values with values used by your provider:

```json
{
  "source": {
    "mode": "Filters",
    "filters": {
      "sensorNames": ["SENSOR_A", "SENSOR_B"],
      "sensorTypes": ["EO"],
      "gridTypes": ["grid"],
      "registrationQualities": ["Accurate"],
      "sortieIds": ["sortie-001"],
      "isRealtime": false,
      "resolution": { "min": 0.3, "max": 1.2 },
      "createdTime": {
        "from": "2026-01-01T00:00:00+02:00",
        "to": "2026-02-01T00:00:00+02:00"
      },
      "photoTime": {
        "from": "2025-01-01T00:00:00Z",
        "to": "2025-12-31T23:59:59Z"
      },
      "wkt": "POLYGON ((34.7 31.7, 34.9 31.7, 34.9 31.9, 34.7 31.9, 34.7 31.7))"
    }
  },
  "targets": [],
  "maxOverlays": 100,
  "publication": { "batchSize": 10, "delayMs": 1000 }
}
```

Filter fields combine with AND. Values within a list are alternatives and are compared exactly, including case. Lists must be nonempty if supplied; registration qualities are `Accurate` and `Sensor`. Resolution bounds must be positive finite JSON numbers. Both numeric and time boundaries are inclusive, and either boundary may be omitted. API timestamps require ISO 8601 seconds with `Z` or an explicit offset; timezone-less request dates are rejected. Use JSON `true` or `false` for `isRealtime`; omitting it means either value.

WKT must be a valid Polygon or MultiPolygon. The search selects intersecting overlays without clipping their footprints. The matched rule's geometry determines the ROI used for dispatch. Explicit filters are checked again against returned overlays locally; an overlay missing a value required by a filter does not match.

`OverlayIds` fetches specific overlays through the provider's GET route. It accepts 1–100,000 nonempty IDs, deduplicates repeated IDs before fetching, and cannot include `filters`. Omit `targets` or send `[]` to prepare ordinary gateway input without local rule matching or per-pipeline contract checks:

```json
{
  "source": {
    "mode": "OverlayIds",
    "overlayIds": ["overlay-001", "overlay-002"]
  },
  "targets": [],
  "publication": { "batchSize": 1, "delayMs": 1000 }
}
```

Direct submission from Filters or Overlay IDs still validates and normalizes provider overlays, reports invalid records or missing explicit IDs, freezes payloads, and enforces overall quotas, memory/scan limits, expiry, and cancellation. Each selected overlay is published as the ordinary JSON body (`id`, `imageUrl`, `roiFootprint`, `sensorName`, and other mapped fields), with the RabbitMQ `x-updated-fields` array containing `gridType`. It has no historical `kind`, `overlay`, or `targets` wrapper and does not require `HistoricalInput.Enabled` on the gateway.

The gateway evaluates its current cached active rules across enabled pipelines, including photo-age conditions. It may dispatch an overlay to multiple matching pipelines or none. Direct previews therefore show no predicted rule assignments or per-rule counts. Gateway rule edits can affect dispatch after preview; only the input payload is frozen.

To retain historical matching for specific IDs, supply targets as before:

```json
{
  "source": {
    "mode": "OverlayIds",
    "overlayIds": ["overlay-001", "overlay-002"]
  },
  "targets": [
    {
      "pipelineId": "FindAir",
      "ruleId": "replace-with-findair-rule-id",
      "minOverlays": 0,
      "maxOverlays": 2
    }
  ],
  "maxOverlays": 2,
  "publication": { "batchSize": 1, "delayMs": 1000 }
}
```

### Quotas, coverage, and preview limits

Each selected target requires `maxOverlays`; its `minOverlays` defaults to zero and must not exceed the maximum. The overall minimum defaults to zero. With targets, the overall maximum defaults to the sum of target maxima, capped by `AutoRun:MaxSelectedOverlays`. Without targets, Overlay IDs defaults to the unique requested ID count, and Filters defaults to 100; both are capped by the same server limit. Direct selection prefers newer photo times, then ordinal overlay ID. A shared overlay counts once toward the overall quota and once toward every rule assigned to it. A single overlay may result in multiple downstream requests through matching rules and their run parameters.

Selection prioritizes overlays that help the most unmet target minimums, then fills available target maxima. Ties prefer newer photo times, then ordinal overlay ID. This is a deterministic greedy selection, not a proof that no other assignment could meet the minimums. Maxima are hard limits; minimum shortfalls are reported per target and overall, and do not prevent starting the available selection. Quotas measure selected overlays, not successful downstream processing.

`counters.scanned` counts processed provider records, including invalid records and duplicates. `observedMatches` counts distinct eligible candidates found before selection; it is not the provider's total hit count. A malformed entity, missing explicit ID, or invalid raw sensor-type result is reported as invalid and preparation continues. A non-retryable provider request failure, malformed search page, or invalid continuation fails preparation. Memory safeguards, the response-size limit, and exhausted HTTP 429/503 retries instead stop collection and preserve eligible candidates already collected for a partial preview.

| Setting | Default | Meaning |
| --- | --- | --- |
| `AutoRun:MaxSelectedOverlays` | 100,000 | Maximum unique selected overlays per run; configurable downward. |
| `AutoRun:MaxScannedRecords` | 1,000,000 | Maximum processed source records per preview; configurable downward. |
| `AutoRun:MaxCandidates` | 200,000 | Maximum eligible candidates retained for selection; configurable downward. |
| `AutoRun:PayloadBudgetBytes` | 536,870,912 | Process-wide retained serialized request, rule, query, diagnostic, and overlay byte budget; configurable downward. |
| `AutoRun:Provider:MaxResponseBytes` | 67,108,864 (64 MiB) | Maximum raw bytes per provider response, including responses without Content-Length; positive and at most 536,870,912 bytes. |
| `AutoRun:Provider:TimeoutSeconds` | 30 | Deadline for each provider HTTP request, including sending the request and downloading its response body; positive. Independent of publication timeout. |
| `AutoRun:Provider:RequestDelayMs` | 1,000 | Minimum gap from completion of one provider attempt to the next provider request; integer 0–60,000. Applies to searches, continuations, ordinary ID lookups, raw sensor-type lookups, and retries. |
| `AutoRun:Provider:MaxRetries` | 2 | Extra attempts after HTTP 429 or 503; integer 0–5. The default allows at most three attempts per request; zero disables retries. |
| `AutoRun:Provider:RetryDelayMs` | 2,000 | Initial retry delay; integer 1–60,000. Doubles for successive retries, capped at 60 seconds, subject to request pacing and `Retry-After` below. |
| `AutoRun:Provider:To` | 1,000 | Value of the provider `to` query parameter on every request, including continuation pages; integer 0 through 10,000. |
| `AutoRun:Memory:LimitBytes` | 0 | Optional additional process-memory ceiling in bytes; zero derives the ceiling from runtime measurements and the following two settings. |
| `AutoRun:Memory:MaxUsagePercent` | 80 | Percentage of runtime-reported available memory allowed before stopping collection; integer 1–99. |
| `AutoRun:Memory:MinimumFreeBytes` | 268,435,456 (256 MiB) | Minimum reserve below runtime-reported available memory; nonnegative. |
| `AutoRun:ReadyLifetimeMinutes` | 30 | Time from readiness until an unstarted preview expires. |
| `AutoRun:SummaryLifetimeMinutes` | 60 | Retention after a terminal state; payloads are released when the run becomes terminal. |
| `AutoRun:MaxRetainedJobs` | 100 | Maximum retained previews and summaries on the instance. |

Reaching a preparation limit produces a partial preview from the eligible candidates already collected, with `sourceExhausted: false`, a warning, and one of these `stoppedReason` values:

| Reason | Collection stopped because |
| --- | --- |
| `scanLimit` | The processed-record limit was reached. |
| `candidateLimit` | The retained-candidate limit was reached. |
| `payloadBudget` | The serialized payload budget could not admit more preparation data. |
| `memoryLimit` | The memory guard could not safely admit more work, or memory measurements were unavailable. |
| `responseLimit` | A provider response exceeded `MaxResponseBytes`. |
| `providerUnavailable` | HTTP 429/503 exhausted the retry budget, or the provider requested a `Retry-After` longer than the supported 60-second wait. |

The UI marks a ready selection with incomplete search coverage as **Partial selection ready**. Review its warnings, diagnostics, and minimum shortfalls, then explicitly start the retained selection; stopping collection never publishes automatically. A preview with zero selected overlays cannot start. Partial results do not establish the source's total count or guarantee the newest overlays outside the scanned subset; later query branches may never have run. An oversized response that was not parsed contributes no records, while candidates from earlier responses remain available. `sourceExhausted: true` means the adapter exhausted the supplied IDs or returned continuation chain; it is not a transactional snapshot of Elasticsearch.

The payload budget counts serialized bytes, not total heap usage: object overhead, geometry, deduplication structures, and transient provider buffers require additional memory. Its 512 MiB maximum remains separate from the configurable provider response limit and the process-memory guard. Provider responses are read into bounded buffers and parsed; batch entities are then normalized and consumed one at a time instead of retaining a second normalized list for the entire page. Raising `MaxResponseBytes` allows larger pages but can increase peak memory while buffering and parsing.

The guard reads `GC.GetGCMemoryInfo().TotalAvailableMemoryBytes` when it initializes. This value reflects the runtime's effective available memory and can be lower than the container's memory limit. Its effective ceiling is the smallest of the percentage limit, available memory minus the free-byte reserve, and a positive explicit `LimitBytes`. For example, if the runtime reports 12 GiB, the default 80% ceiling is 9.6 GiB; a lower runtime report produces a lower ceiling. Kubernetes CPU and memory requests do not automatically change these application settings.

Before buffering, parsing, and retaining additional data, admission checks compare estimated allocations against remaining headroom. Current usage is the larger of process working set and managed-memory usage; the managed heap is already part of process memory and is not added twice. These checks are conservative estimates, not an OOM guarantee: concurrent service work, native allocations, geometry parsing, and garbage collection can change memory between checks. Keep reserve for those operations and monitor actual deployment usage before increasing limits. Terminal jobs release overlay payload references immediately; garbage collection reclaims their memory later. Retained requests, rule snapshots, queries, and diagnostics remain charged until deletion or summary expiry.

Options and run summaries expose `memory.availableBytes`, `processBytes`, `managedBytes`, `limitBytes`, `headroomBytes`, and `isUnderPressure`. `availableBytes` is the initialization-time capacity measurement, while the usage fields are current process measurements. A negative usage value means the measurement was unavailable; the guard stops further collection. The UI displays process usage, the safeguard ceiling, remaining headroom, and partial-selection warnings.

### Publication and outcomes

`publication` may be omitted to use configured defaults. `batchSize` is 1–100 overlays, and `delayMs` is 0–60,000 milliseconds between batches. The default is 10 overlays and a 1,000 ms delay. A batch waits for its publication outcomes before the next batch; actual throughput also depends on broker latency, `AutoRun:PublisherConcurrency` (default 4), and `AutoRun:PublishTimeoutSeconds` (default 30).

To change a run's publication settings, send:

```http
PATCH /auto-runs/{id}/publication
Content-Type: application/json

{ "batchSize": 20, "delayMs": 500 }
```

During publication, changes apply at the next batch boundary. The summary exposes requested `publication`, effective `activePublication`, and `publicationChangePending`. Changing settings does not change the selected overlays, rule snapshots, or quotas.

Each selected overlay produces one persistent, mandatory RabbitMQ message. Filters and Overlay IDs without rules use the frozen ordinary overlay body; requests with selected targets use a historical envelope containing the frozen overlay and assigned rule snapshots. Both paths use broker confirmations and the same publication controls. Item outcomes have these meanings:

| Outcome | Meaning |
| --- | --- |
| `Pending` | Publication has not started for this item. |
| `InFlight` | Publication is awaiting an outcome. |
| `Confirmed` | The broker confirmed publication to the gateway input route. |
| `Failed` | The message could not be constructed, the destination was invalid, or the broker explicitly rejected or returned it. |
| `Uncertain` | A timeout, cancellation, connection failure, or lost confirmation prevents determining delivery. It may already be queued. |

`Completed` means all selected publications were confirmed; `CompletedWithErrors` means publication finished with failed or uncertain items. Neither state means the gateway or downstream algorithms finished processing. Per-rule confirmed counts inherit the result of the shared gateway envelope. The Rules API does not automatically retry failed or uncertain publications. Retrying start on the same run does not resend messages; creating a new run is a deliberate new execution.

Historical gateway retries preserve dispatch IDs for the same run, overlay, pipeline, rule, and run parameters. A new historical run has new dispatch IDs. Direct input follows ordinary gateway identity: the same overlay, pipeline, rule, and run parameters reuse downstream dispatch IDs across auto-runs, so downstream deduplication may suppress repeat processing. Broker message IDs and correlation IDs remain scoped to each auto-run in both modes. A gateway retry can resend targets that already succeeded; see [historical gateway input](../unified-gateway/README.md#historical-auto-run-input).

### Elastic Provider configuration and contract

The adapter uses:

- `GET /ErisImages/v2.0/ErisImages/Images/TableEntities/{id}` for one overlay.
- `POST /ErisImages/v2.0/ErisImages/Images/TableEntities` with `Content-Type: application/json-patch+json` for filtered batches, including continuation requests.
- `GET /ErisImages/v2.0/ErisImages/Images/TableEntities/{id}?to=1000&result_type=4` to resolve the actual sensor type before retaining an eligible overlay. The `to` value uses the configured setting.

Every provider request includes `to=<AutoRun:Provider:To>`, defaulting to `to=1000`. Set this integer to any value from 0 through 10,000 in configuration, or use `AutoRun__Provider__To`, then restart Rules API. Zero is sent as `to=0`; it does not omit the parameter or mean unlimited locally. The provider determines the result of that value. This page-size control is separate from the overall selected-overlay quota and the byte limit on each response.

Batch responses must contain `entities_list`; the optional `next_page` URL is followed with the **same POST body**. The configured `to` replaces any existing `to` parameter on continuation URLs; all other query components, including encoded cursor values, are preserved. Continuations must remain on the configured scheme, host, and port. Redirects are disabled, repeated continuation URLs fail preparation, and an empty `entities_list` terminates paging. Provider request timeout defaults to 30 seconds.

For slow provider searches or large pages, set `AutoRun:Provider:TimeoutSeconds` to a larger value, such as `120`, and restart Rules API. The deadline resets for each lookup or page request and covers waiting for response headers plus downloading the body; it is not a deadline for the entire preparation. Increasing `MaxResponseBytes` does not increase this timeout. `AutoRun:PublishTimeoutSeconds` only controls RabbitMQ publication.

`GET /auto-runs/options` exposes the effective `provider.to`, `provider.timeoutSeconds`, `provider.maxResponseBytes`, `provider.requestDelayMs`, `provider.maxRetries`, `provider.retryDelayMs`, and `provider.sensorTypeMappings`. Timeout errors identify an overlay lookup, sensor type lookup, initial search page, or continuation page, whether the request was waiting for headers or reading the body, and how many body bytes were read. They do not include request URLs, cursor values, or headers. A transport timeout or network failure fails preparation and is not automatically retried. Provider HTTP 408/504 responses are reported as HTTP errors; a larger client timeout cannot override a proxy or provider's own deadline. Narrowing the time range or search area can reduce search work; selected-overlay quotas do not control provider page size.

Provider requests are sequential and paced with `RequestDelayMs`, separately from RabbitMQ publication's `publication.delayMs`. The first request can start immediately; subsequent attempts wait until the configured gap after the previous attempt's completion. HTTP **429 and 503 only** are retried, using the same URL, method, body, and configured headers. The default `MaxRetries: 2` and `RetryDelayMs: 2000` allow three total attempts, with retry delays of 2 seconds and 4 seconds. Each attempt gets a fresh `TimeoutSeconds` deadline after the wait. Cancellation interrupts either the request or its wait.

For 429/503, a valid `Retry-After` in delta-seconds or HTTP-date form can extend the retry wait. The effective wait is the larger of the remaining request-pacing gap, exponential retry delay, and server-requested wait. Retry waits are bounded at 60 seconds. A server request to wait longer stops preparation with `providerUnavailable` instead of retrying early; an absent, invalid, or past `Retry-After` adds no wait. Other HTTP statuses, malformed JSON, and transport failures are not retried. These settings do not cause publication retries or an automatic start of a partial preview. Restart Rules API after changing them, or supply environment variables such as `AutoRun__Provider__RequestDelayMs`, `AutoRun__Provider__MaxRetries`, and `AutoRun__Provider__RetryDelayMs`.

Optional `AutoRun:Provider:Headers` configures string header names and values for every provider GET, search POST, and continuation request. The default is `{}`. For example:

```json
{
  "AutoRun": {
    "Provider": {
      "Headers": {
        "X-Sending-System": "UnifiedGateway",
        "Authorization": "Bearer replace-me"
      }
    }
  }
}
```

Supply real credentials through deployment secrets, for example the environment variable `AutoRun__Provider__Headers__Authorization`. Header names are case-insensitive; values are preserved. Malformed headers and content/transport-managed headers such as `Content-Type`, `Content-Length`, `Host`, and `Connection` fail startup validation. The adapter sets `application/json-patch+json` itself for POST bodies; do not put `Content-Type` in the configured header dictionary. Provider request logging uses sanitized operation context instead of raw request URLs or headers. Configured header values reflected in error bodies are redacted before those excerpts reach the Auto-run API, UI, or application logs. Restart Rules API after changing these settings. Continuation requests remain restricted to the configured origin, and redirects remain disabled.

Request construction follows the supplied working logical-operator example, not the differing Swagger skeleton:

```json
{
  "filter": {
    "logical_operators": {
      "AND": [
        { "match": { "SENSOR": { "type": "IN", "values": ["ar"] } } },
        { "range": { "RESOLUTION": { "type": "IN", "values": [{ "gte": 1, "lte": 20 }] } } },
        { "range": { "date": { "type": "IN", "values": [{ "gte": "2026-10-04T16:40:00.000Z", "lte": "2026-10-05T16:43:00.999Z" }] } } }
      ]
    }
  },
  "sort": { "date": "desc" }
}
```

Different rule/sensor branches use separate AND queries, and each query's entire continuation chain finishes before the next query begins. The adapter does not invent an OR grammar or retry by dropping a rejected condition. Provider request aliases are configurable under `AutoRun:Provider:RequestFields`:

| Option | Default request alias |
| --- | --- |
| `Id` | `GlobalId` |
| `SensorName` | `SENSOR` |
| `SensorType` | `sensorType.keyword` |
| `GridType` | `gridType.keyword` |
| `RegistrationQuality` | `hasalgorithmicregistration` |
| `SortieId` | `SORTIE` |
| `IsRealtime` | `ISREALTIME` |
| `CreatedTime` | `CREATED` |
| `PhotoTime` | `date` |
| `Resolution` | `RESOLUTION` |
| `Geometry` | `geo` |
| `SortDate` | `date` |
| `ModeSar` | Unset; SAR mode is checked locally. |

The UI's **Photo taken from/to** bounds use the provider search field `date` (or `DATE` through `RequestFields.PhotoTime`), distinct from the response property `PhotoTime`. **Created from/to** bounds use `CREATED` by default; `RequestFields.CreatedTime` can select the provider's `created` or `updated` alias. These settings name the outbound query fields; they do not change the separately configured response fields, whose defaults remain `PhotoTime` and `Created`.

Registration quality is encoded as native JSON booleans in provider queries: `Accurate` becomes `true`, `Sensor` becomes `false`, and selecting both produces both values. For example:

```json
{ "match": { "hasalgorithmicregistration": { "type": "IN", "values": [true, false] } } }
```

This conversion applies to each saved rule's sensor conditions and to explicit registration-quality filters. The UI, preview request, local matcher, and response property `properties_list.RegistrationQuality` continue to use `Accurate`/`Sensor`.

All generated photo-time and creation-time bounds are converted to UTC and formatted with exactly three fractional digits and a `Z` suffix: `yyyy-MM-ddTHH:mm:ss.fffZ`. For example, `2026-10-04T19:40:00+03:00` becomes `2026-10-04T16:40:00.000Z`.

**Deployment migration:** if configuration explicitly sets `AutoRun:Provider:RequestFields:PhotoTime` to `PHOTOTIME`, remove that override to use the corrected `date` default, or change it to `date`/`DATE`. This includes the environment variable `AutoRun__Provider__RequestFields__PhotoTime`. Existing explicit aliases take precedence over code defaults. Leave `ResponseFields:PhotoTime` as `PhotoTime` unless your provider response uses a different property.

Also remove or update any explicit `RequestFields:RegistrationQuality` value of `REGISTRATIONQUALITY`, including `AutoRun__Provider__RequestFields__RegistrationQuality`, so queries use `hasalgorithmicregistration`. Explicit aliases still override the default; changing the alias does not revert the boolean encoding. Keep `ResponseFields:RegistrationQuality` unchanged unless the response property itself differs.

Grid-type searches use `RequestFields:GridType = "gridType.keyword"` for both saved-rule conditions and explicit API filters. Remove an old `SOLUTIONTYPE`/`solutionType` override or set `AutoRun__Provider__RequestFields__GridType=gridType.keyword`. The response mapping remains independent: `ResponseFields:GridType` still defaults to `SolutionType`. If the provider returns `properties_list.gridType` instead, set `ResponseFields:GridType` to `gridType`; `.keyword` is the query field suffix, not an assumed response property.

Sensor-type searches use `RequestFields:SensorType = "sensorType.keyword"`. Remove an explicit `IMAGINGTECHNIQUE` override or set `AutoRun__Provider__RequestFields__SensorType=sensorType.keyword`. For example, the default catalog turns a VIS/PAN selection into:

```json
{ "match": { "sensorType.keyword": { "type": "IN", "values": ["VIS", "PAN"] } } }
```

`AutoRun:Provider:SensorTypeMappings` defines case-sensitive public sensor-type names and their provider query values. Its default identity mappings are `VIS`, `PAN`, `PAN_Sharpen_RGB`, `PAN_MSI`, `EO`, `IR`, `CIR`, `NIR`, `MWIR`, `SAR`, and `SWIR`, each mapped to the same string. Supplying the dictionary replaces the default catalog, so include every allowed type. It must contain 1–128 entries; keys and values must be nonblank, at most 128 characters, and have no surrounding whitespace. Unsupported filter types or selected-rule `match.sensorTypes` fail preview validation before provider work begins. This validation applies to AutoRun preparation, not general rule CRUD.

Mappings affect outbound query values only. Preview requests, rule snapshots, and local matching keep their original public type names, and the UI displays the provider query exactly as sent. The raw overlay's actual `sensorType` is not renamed through this dictionary; configure public names to agree with those actual values. A configured subset can be supplied as `"SensorTypeMappings": { "VIS": "VIS", "PAN": "PAN" }` under `AutoRun:Provider`.

Other request aliases remain deployment assumptions. Confirmed aliases include `SENSOR`, `geo`, `sensorType.keyword`, `gridType.keyword`, `hasalgorithmicregistration`, photo-time `date`/`DATE`, and the creation-time aliases above, together with the logical grammar; not every alias in the table has been verified. Requests always use native JSON numbers and booleans, such as `1`, `20`, and `false`.

The response contract is separate from request aliases. The ordinary GET and each search batch entity contain `properties_list` and `geo.wkt`. The additional `result_type=4` GET has the distinct raw shape described below. These configured mappings normalize the ordinary response:

| Provider response field | Normalized overlay field |
| --- | --- |
| `properties_list.GlobalId` | `id` |
| `properties_list.Url` | `imageUrl` |
| `properties_list.GridPath` | `gridURI` |
| `properties_list.Sensor` | `sensorName` |
| `properties_list.ImagingTechnique` | Initial coarse `sensorType`; replaced by the raw lookup before matching/selection. |
| `properties_list.SolutionType` | `gridType` |
| `properties_list.Sortie` | `sortieId` |
| `properties_list.Resolution` | `bestResolution` |
| `properties_list.RegistrationQuality` | `registrationQuality` |
| `properties_list.PhotoTime` | `photoTime` |
| `properties_list.Created` | `createdTime` |
| `properties_list.IsRealTime` | `isRealtime` |
| `properties_list.ImageWidth`, `properties_list.ImageHeight` | `width`, `height` |
| `geo.wkt` | Polygon/MultiPolygon GeoJSON `roiFootprint` |

Response aliases can be changed under `AutoRun:Provider:ResponseFields` using the option names in [ElasticProviderOptions.cs](AutoRuns/Provider/ElasticProviderOptions.cs). The GET result's mapped `GlobalId` must equal the requested ID. Response resolution must be a JSON number, and `IsRealTime`, when present, must be a JSON boolean.

The search projection can collapse VIS, PAN, and other optical types into a coarse `ImagingTechnique: "EO"`. AutoRun therefore deduplicates and checks the non-type criteria first, then performs a raw `result_type=4` GET for each remaining candidate before evaluating sensor-type conditions, pipeline validation, quota assignment, or direct selection. This also applies to Overlay IDs, where the ordinary overlay GET is followed by the raw lookup. A query branch or selected rule is never used to infer the actual type.

The raw response must be a JSON object with a nonempty root `id` equal to the requested overlay ID and a nonempty string `sensorType`, for example `{ "id": "overlay-001", "sensorType": "PAN" }`. Other raw fields are ignored. Only the normalized payload's `sensorType` and the in-memory matching input's `SensorType` are overwritten; image URLs, dimensions, geometry, grid data, and other normalized fields stay from the ordinary response. A missing overlay, mismatched ID, malformed raw JSON, or absent/invalid type is counted as an invalid record and cannot consume quotas. HTTP failures follow the retry/failure policy above. No UnifiedGateway behavior or message contract changes are required for this resolution step.

This adds one GET for each distinct candidate that passes non-type checks, including direct submissions without a sensor-type filter. Those lookups share the provider pacing, timeout, and retry policy. Search pages and their candidates are processed sequentially; `RequestDelayMs: 1000` therefore adds roughly one second of pacing per lookup, before network latency, plus page requests and any retries. Preparation can take much longer than a page-only search even when the final selection quota is small. Narrow the source criteria and size the provider's capacity before reducing pacing or increasing scan limits.

Every normalized overlay requires a nonempty `gridType`, including direct Filters and Overlay IDs submissions. Saved sensor conditions can restrict grid types, local matching verifies those restrictions, and FindAir includes grid type in its downstream payload. Changing the outbound query alias does not remove this input requirement. A provider response missing its configured grid-type property is counted as invalid.

For FindAir, mapped `ImageWidth` and `ImageHeight` must be positive whole-number JSON values within the 32-bit range (1 through 2,147,483,647). The adapter accepts both integer and decimal representations, such as `40` and `40.0`, and emits integer `width` and `height` in the normalized gateway payload. For example, `8836.0` by `3608.0` becomes `8836` by `3608`. Fractional values such as `40.5` are never rounded; quoted numbers, nonpositive dimensions, and out-of-range values still fail FindAir validation. Check `ResponseFields:Width` and `ResponseFields:Height` if the provider uses other property names. Preview errors identify the normalized fields (`input.width` and `input.height`); rejected candidates cannot be assigned to FindAir, and a preview with no selected overlays cannot start.

Provider dates without offsets are interpreted as Israel local time (`Asia/Jerusalem`) in code. Dates carrying explicit offsets honor those offsets. Ambiguous or nonexistent daylight-saving times make that record invalid instead of being guessed. Accepted local date shapes are `dd/MM/yyyy HH:mm:ss` and `yyyy-MM-ddTHH:mm:ss`, optionally with fractional seconds.

The provider timezone and string-encoding flags remain fixed in code. Remove old `SourceTimeZoneId`, `StringEncodedNumbers`, and `StringEncodedBooleans` entries from deployment configuration; they no longer affect behavior. `AutoRun:Provider:MaxResponseBytes` is configurable again and defaults to 64 MiB. An existing explicit value overrides that default, so remove an old 16 MiB override or raise it deliberately if larger provider pages are needed.

Optional `ResponseFields.ModeSar`, `AreaOfInterest`, `LegId`, `PrevOverlayId`, and `NextOverlayId` are unset until their real provider property names are configured. Rules with `modeSAR` always enforce that condition locally; configure its response alias before expecting matches, and configure `RequestFields.ModeSar` separately only if provider filtering is verified. Missing SAR metadata does not silently disable the rule condition.

Shared overlay validity is checked first, then each selected pipeline validates the fields it needs. FindAir checks image URL, dimensions, sensor type, grid URI, and optional area metadata. DeepWisdom checks relationship metadata for `runEveryOtherImage` when supplied. SarAtc gets `modelName` and `fieldsToFilter` from the saved rule's run parameters, not an invented provider field. An overlay can remain eligible for one target when another target's contract rejects it. See [pipeline contracts](../../libs/pipeline-contracts/README.md) for exact downstream requirements.

### Destination configuration and rollout

The auto-run destination is the **Unified Gateway input queue**, independently of pipeline catalog transport destinations. This example matches the repository's default gateway input topology; replace host and credentials for the deployment:

```json
{
  "AutoRun": {
    "Enabled": false,
    "Publication": { "BatchSize": 10, "DelayMs": 1000 },
    "PayloadBudgetBytes": 536870912,
    "Ui": { "MapUrl": "" },
    "Memory": {
      "LimitBytes": 0,
      "MaxUsagePercent": 80,
      "MinimumFreeBytes": 268435456
    },
    "Provider": {
      "BaseUrl": "https://elastic-provider.example",
      "TableEntitiesPath": "/ErisImages/v2.0/ErisImages/Images/TableEntities",
      "To": 1000,
      "TimeoutSeconds": 30,
      "MaxResponseBytes": 67108864,
      "Headers": {}
    },
    "Destination": {
      "Connection": {
        "Name": "unified-gateway-input",
        "Host": "rabbitmq",
        "Port": 5672,
        "Username": "replace-me",
        "Password": "replace-me",
        "VirtualHost": "/"
      },
      "QueueName": "unified-gateway.input.q",
      "QueueArguments": {
        "x-dead-letter-exchange": "",
        "x-dead-letter-routing-key": "unified-gateway.dlq.q"
      },
      "ExchangeName": "",
      "ExchangeType": "direct",
      "RoutingKey": "",
      "BindQueueToExchange": false,
      "ExchangeArguments": {},
      "BindingArguments": {}
    }
  }
}
```

Use deployment secrets for credentials, for example `AutoRun__Destination__Connection__Password`. `Connection.Name` is a required logical connection name. An empty `ExchangeName` publishes through the default exchange using `QueueName` as the effective routing key.

The publisher declares a durable, nonexclusive, non-auto-delete queue on first use. `QueueArguments` must match the gateway queue's existing declaration, including its `RabbitMq:HeadersArguments`, effective `x-dead-letter-exchange`, effective `x-dead-letter-routing-key`, and any explicit `x-queue-type`. Copy the deployed values, not just the example. For a named exchange, configure `ExchangeName`, `ExchangeType`, and `ExchangeArguments` to match it; set `RoutingKey` to the gateway input route, and use `BindQueueToExchange` with matching `BindingArguments` if this publisher should declare the binding. Named exchanges are declared durable and non-auto-delete. Incompatible declarations fail; the publisher does not replace existing broker topology.

Argument dictionaries are JSON objects containing scalar strings, booleans, integers, or finite numbers, rather than serialized JSON strings or nested objects. They are validated and normalized at startup. String-valued broker arguments such as `x-queue-type` and dead-letter exchange/routing keys must remain strings.

Deployment sequence:

1. For runs with selected rules, deploy the gateway version supporting historical envelopes to **every consumer** of the input queue and enable `HistoricalInput__Enabled=true` before publication. Filters and Overlay IDs without rules use ordinary input and do not require this flag or historical-envelope support.
2. Deploy one Rules API instance with `AutoRun:Enabled=false`, configure its provider and exact gateway destination, and verify the enabled pipeline IDs and contracts agree with the gateway.
3. Verify the real provider contract in the deployment: field casing, numeric resolution bounds, both boolean values, date ranges/timezone, Polygon and MultiPolygon intersections, required response mappings, and at least two pages followed by termination. Confirm `sensorType.keyword` filtering and the additional `result_type=4` GET's root `id`/`sensorType`, including a VIS/PAN result whose ordinary projection says EO. Check that these raw lookups preserve the search continuation and measure their latency/load. Exercise 429/503 with the deployment's actual `Retry-After` behavior and verify a partial preview after exhausted retries. Check returned values, not only HTTP success.
4. Enable `AutoRun__Enabled=true`, prepare a small preview, inspect its assigned targets and warnings, then start it. Verify broker confirmations and correlated gateway dispatch before increasing quotas or batch size.

The implementation has local automated coverage; **the real Elastic Provider, RabbitMQ deployment topology, and end-to-end dispatch have not been validated against a live deployment**. A ready preview validates available overlay data and, for selected-rule runs, their pipeline contracts, but does not establish broker connectivity. Scaling this version to multiple Rules API replicas is unsupported.

### Auto-run observability

Preparation and publication emit `auto_run.prepare` and `auto_run.publish` spans with `auto_run.id`. Information logs report admission, preparation/page progress, readiness, publication, settings changes, cancellation, final counts, and data release. Detailed per-overlay normalization/matching activity is available at Debug; rejected or uncertain publication and provider failures retain warning/error context. Correlate using the run and trace IDs. The Rules API meter exposes `auto_run.selected.overlays` and `auto_run.publications`; the latter has a bounded `outcome` dimension. IDs remain in spans/logs and are not metric labels.

Provider telemetry adds `auto_run.provider.requests`, `auto_run.provider.retries`, and `auto_run.provider.request.duration` in milliseconds. Each completed attempt records operation, HTTP method/status, and outcome; retry counts reflect actual extra attempts. Raw sensor-type lookup completion logs use Debug to keep per-candidate traffic out of normal Information logs.

The summary retains `warnings` and `errors` for existing clients and adds `diagnostics`, `diagnosticsOmitted`, and nullable `providerProgress`. A diagnostic carries a timestamp, severity, stable code, stage, readable message, trace ID, and available query/page/overlay/pipeline/rule context. Provider failures also carry an `upstream` object with operation, HTTP method/status, attempt count, elapsed time, content type, response bytes read, and a sanitized response-body excerpt with truncation/unavailability flags. The UI displays JSON or plain-text excerpts as escaped text under expandable details. Synchronous AutoRun errors retain `error` and add code/trace context when available; validation fields remain visible.

Error-body capture reads at most **8 KiB**, or the lower configured `MaxResponseBytes`; it never reads an unbounded error response. Stored text is also bounded after redaction. A failed/timed-out body read preserves the HTTP status and marks unavailable or partial content. Credentials, secret-bearing fields, request URLs, and continuation cursors are redacted rather than exposed as troubleshooting context. Diagnostics retain at most **50 entries and 64 KiB of serialized UTF-8 per run**, with at most 12 KiB per entry. Messages are capped at 1,000 characters; oversized excerpts may be omitted, important provider/final failures can displace older record warnings, and `diagnosticsOmitted` counts dropped entries. Global retained-data and memory limits can further reduce these samples. Diagnostic bytes are included in the run's `retainedBytes` and the shared payload budget, stay through terminal summary retention, and are released on deletion or expiry.

During preparation, `providerProgress` describes the latest operation, not an event history. Its phase is `requesting`, `requestDelay`, or `retryWait`, with attempt/max-attempt counts, the last HTTP status when present, and `nextAttemptAt` for a scheduled wait. Query/page indices are one-based when applicable. The UI polls this state to explain waiting and retry countdowns while keeping collected counts visible. A failed provider page does not look like a successful empty search.

The summary also exposes current counts, per-target shortfalls, `confirmedPerSecond`, current memory measurements, `sourceExhausted`, and `stoppedReason`. Gateway telemetry identifies historical envelopes through `auto_run.run_id` and `unified_gateway.input_kind`. Direct input follows ordinary gateway telemetry, with the run ID retained in the message correlation ID. Use gateway events to distinguish confirmed enqueueing from later dispatch or downstream failures; AutoRun's provider changes do not alter gateway behavior.

## Rule Schema

Rule documents use this JSON shape:

```json
{
  "_id": "rule-001",
  "ruleName": "find-cars-tel-aviv",
  "description": "Find vehicles in Tel Aviv",
  "algorithmName": ["FindAir", "Rpn"],
  "sensors": {
    "camera": ["rgb-main", "rgb-backup"],
    "satellite": ["sentinel-2"]
  },
  "isActive": true,
  "tenantsInfo": [
    {
      "tenantId": "tenant-a",
      "tilingConfigs": [
        {
          "tileSizeWidth": 512,
          "tileSizeHeight": 512,
          "tileOverlapWidth": 32,
          "tileOverlapHeight": 32
        }
      ]
    }
  ],
  "minimumResolution": 0.5,
  "maximumResolution": 2.5,
  "area": "tel-aviv",
  "locationWkt": "POINT (1 1)",
  "locationGeoJson": {
    "type": "Point",
    "coordinates": [1, 1]
  },
  "isPhotoOld": false,
  "creationTime": "2026-06-26T10:00:00Z",
  "updateTime": "2026-06-26T10:00:00Z"
}
```

Required validation:

- `_id` is generated by Elasticsearch when a rule is created.
- `ruleName` cannot be empty and must be unique.
- `algorithmName` is a required, nonempty array. It accepts `["FindAir"]`, `["Rpn"]`, or both values. Values must be unique.
- `tenantsInfo` must contain at least one tenant with a nonempty `tenantId` and at least one tiling configuration.
- Tile dimensions must be greater than `0`; tile overlaps cannot be negative.
- `minimumResolution` and `maximumResolution` must be greater than `0`.
- `maximumResolution` must be greater than or equal to `minimumResolution` and defaults to `999`.
- At least one geometry field must be provided: `locationWkt`, `locationGeoJson`, or both.

Notes:

- The JSON field names for resolution are `minimumResolution` and `maximumResolution`.
- The create request contract excludes `_id`, `creationTime`, and `updateTime`; these values are owned by the server. Rule IDs cannot be changed through update routes.
- `_id` is returned from Elasticsearch metadata and is not stored inside the document `_source`.
- `creationTime` and `updateTime` are set by the service when creating a rule.
- `updateTime` changes whenever a rule is updated or sensor values are mutated.
- Sensor keys with empty names are removed during normalization.
- Empty sensor values and duplicate sensor values are removed during normalization.

## Response Shapes

Error response:

```json
{
  "error": "Rule 'rule-001' was not found."
}
```

Bulk operation response:

```json
{
  "successIds": ["rule-001"],
  "failedIds": [
    {
      "id": "missing-rule",
      "reason": "Rule not found"
    }
  ]
}
```

Repository failures, usually Elasticsearch failures, return `503 Service Unavailable`.

## Observability

The API exports traces to the OpenTelemetry Collector over OTLP, exposes metrics at `/metrics` for Prometheus, and sends correlated ECS JSON logs to Logstash over HTTP.

- ASP.NET Core produces the HTTP server spans and request-duration metrics; a separate request-logging middleware is intentionally not used.
- Rules actions add child spans and bounded metrics for operation outcome, duration, validation failures, requested bulk size, and returned or changed document counts.
- Elasticsearch client operations add dependency spans and metrics for get, search, index, and delete calls.
- Rule and document IDs are attached only to spans and logs, never to metric labels.
- Health-check success traffic is excluded from traces and does not produce success logs; failures still emit warnings.
- Elasticsearch failures include exception details in server telemetry, while clients receive only a stable generic error.
- Request bodies, credentials, and Elasticsearch debug payloads are not logged.

Debug logs are disabled by default. Enable them temporarily for the Rules API category:

```bash
Logging__LogLevel__ImagingPipeline.Rules.Api=Debug
```

Trace and span identifiers are supplied by OpenTelemetry log correlation rather than repeated in message templates.

## Routes

### GET /pipelines

Lists every configured pipeline, enabled or not, ordered by ID. Transport settings are never returned.

```json
[
  { "pipelineId": "DeepWisdom", "contractId": "DeepWisdom", "enabled": true },
  { "pipelineId": "FindAir", "contractId": "FindAir", "enabled": true }
]
```

### GET /pipelines/{pipelineId}

Returns one pipeline in the same shape, or `404 Not Found` when the ID is not in the catalog.

### GET /pipelines/{pipelineId}/rules

Lists the pipeline's v2 rules (`PipelineRuleDocument`) from the index the catalog names for it. Accepts the same `getNameOnly`, `isActive`, `from` and `size` query parameters as `GET /rules`.

### GET /pipelines/{pipelineId}/rules/{id}

### GET /pipelines/{pipelineId}/rules/name/{ruleName}

Return one v2 rule, or `404 Not Found`.

### POST /pipelines/{pipelineId}/rules

Creates a v2 rule from `ruleName`, `description`, `isActive` (default `true`), `matchAll`, `match` and `runParams`. The server sets the ID, `schemaVersion` and timestamps, and derives `match.location` from `match.locationWkt`. The rule is checked with the same validator the gateway uses when it loads rules: the document rules, then the pipeline contract's run parameters and location requirement. Returns `201 Created`, `400 Bad Request` with the validation errors, or `409 Conflict` when the name is already used in this pipeline. Creates, deletes and renames wait for the index refresh, so the next request's name check sees them. An update that changes nothing is not saved.

### PATCH /pipelines/{pipelineId}/rules/{id}

Partial update. Only the fields sent change; a field sent as `null` is cleared. Fields inside `match` are patched one by one, so `{ "match": { "maxPhotoAgeDays": 7 } }` keeps the other conditions, and `{ "match": null }` removes them all. The updated rule must pass the same validation as on create.

### PATCH /pipelines/{pipelineId}/rules/{id}/activity

Body `{ "isActive": true|false }`. Activation is validated like any other update. Deactivation is not, so a rule that stopped passing validation, for example after a contract change, can still be switched off.

### PATCH /pipelines/{pipelineId}/rules/bulk?ids=a,b

Applies one partial update, in the same shape as `PATCH /pipelines/{pipelineId}/rules/{id}`, to each listed rule. Each rule is validated and saved on its own. Returns `200 OK` when all succeed, `207 Multi-Status` when some fail and `422 Unprocessable Entity` when all fail, with `successIds` and `failedIds` (each with a reason). Setting `ruleName` on more than one rule returns `409 Conflict`. Duplicate IDs are applied once, and more than `Elasticsearch:MaxBulkSize` IDs (default 500) returns `400 Bad Request`. A rule that cannot be saved is listed in `failedIds` while the others are still saved.

### PATCH /pipelines/{pipelineId}/rules/sensors/add?ids=a,b

### PATCH /pipelines/{pipelineId}/rules/sensors/remove?ids=a,b

Body `{ "sensorName": "...", "registrationQualities": [...], "gridTypes": [...] }`, the same as the v1 routes. Add merges the values into the sensor in `match.sensors`, creating it when missing. Remove takes the values out and drops a sensor left with none. Results are reported per rule, as in bulk update. Two cases fail for that rule instead of changing what it matches:

- Removing a rule's last sensor, which would make it match every sensor. To do that on purpose, send `match.sensors` as `null` in a PATCH.
- Adding a sensor to a `matchAll` rule, which cannot have conditions.

Adding a sensor to a rule with no sensor condition limits it to that sensor.

### DELETE /pipelines/{pipelineId}/rules/{id}

Deletes the rule permanently. Returns `204 No Content` or `404 Not Found`.

Rule writes log events 5031–5037 with `pipeline.id`, `findair.rule.id` and, where relevant, `unified_gateway.rule.changed_fields` or `unified_gateway.rule.validation.errors` as fields. Bulk requests skip per-rule not-found warnings and log one summary (5038) with the operation, requested, succeeded and failed counts, and up to 10 failed IDs.

Every `/pipelines/{pipelineId}/...` route answers `404 Not Found` for a pipeline that is not in the catalog, without querying Elasticsearch, and logs event 5030 with `pipeline.id` as a field.

### GET /health

Checks whether the API process can connect to Elasticsearch.

Response `200 OK`:

```json
{
  "status": "Healthy"
}
```

When Elasticsearch is unavailable, the endpoint returns `503 Service Unavailable`:

```json
{
  "status": "Unhealthy"
}
```

### GET /rules

Returns rule documents.

Query parameters:

- `getNameOnly`: optional boolean. Defaults to `false`.
- `isActive`: optional boolean. When set, filters active or inactive rules.
- `from`: optional zero-based result offset. Defaults to `0`.
- `size`: optional page size. Defaults to `Elasticsearch:DefaultSearchSize` and cannot exceed `Elasticsearch:MaxSearchSize`.

Examples:

```bash
curl "http://localhost:8080/rules"
curl "http://localhost:8080/rules?isActive=true"
curl "http://localhost:8080/rules?getNameOnly=true&isActive=true"
curl "http://localhost:8080/rules?from=100&size=100"
```

Response when `getNameOnly=false`:

```json
[
  {
    "_id": "rule-001",
    "ruleName": "find-cars-tel-aviv",
    "algorithmName": ["FindAir"],
    "sensors": {},
    "isActive": true,
    "tenantsInfo": [
      {
        "tenantId": "tenant-a",
        "tilingConfigs": [
          {
            "tileSizeWidth": 512,
            "tileSizeHeight": 512,
            "tileOverlapWidth": 32,
            "tileOverlapHeight": 32
          }
        ]
      }
    ],
    "minimumResolution": 0.5,
    "maximumResolution": 2.5,
    "area": "tel-aviv",
    "locationWkt": "POINT (1 1)",
    "creationTime": "2026-06-26T10:00:00Z",
    "updateTime": "2026-06-26T10:00:00Z"
  }
]
```

Response when `getNameOnly=true`:

```json
["find-cars-tel-aviv"]
```

Status codes:

- `200 OK`
- `400 Bad Request` for invalid boolean query values.
- `503 Service Unavailable` when Elasticsearch access fails.

### GET /rules/{id}

Returns one rule by `_id`. Route matching is case-sensitive for ids.

Example:

```bash
curl "http://localhost:8080/rules/rule-001"
```

Status codes:

- `200 OK` with a rule document.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### GET /rules/name/{ruleName}

Returns one rule by `ruleName`. Name matching is case-sensitive.

Example:

```bash
curl "http://localhost:8080/rules/name/find-cars-tel-aviv"
```

Status codes:

- `200 OK` with a rule document.
- `404 Not Found` when the name does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### POST /rules

Creates a new rule.

Request body: `CreateRuleRequest`, containing only client-controlled rule fields. The contract excludes `_id`, `creationTime`, and `updateTime`.

Example:

```bash
curl -X POST "http://localhost:8080/rules" \
  -H "Content-Type: application/json" \
  -d '{
    "ruleName": "find-cars-tel-aviv",
    "description": "Find vehicles in Tel Aviv",
    "algorithmName": ["FindAir", "Rpn"],
    "sensors": {
      "camera": ["rgb-main", "rgb-main", "rgb-backup"]
    },
    "isActive": true,
    "tenantsInfo": [
      {
        "tenantId": "tenant-a",
        "tilingConfigs": [
          {
            "tileSizeWidth": 512,
            "tileSizeHeight": 512,
            "tileOverlapWidth": 32,
            "tileOverlapHeight": 32
          }
        ]
      }
    ],
    "minimumResolution": 0.5,
    "maximumResolution": 2.5,
    "area": "tel-aviv",
    "locationWkt": "POINT (1 1)",
    "isPhotoOld": false
  }'
```

On success, Elasticsearch generates `_id`, and `creationTime` and `updateTime` are set to the current UTC time. Duplicate sensor values are normalized.

Status codes:

- `201 Created` with the saved rule.
- `400 Bad Request` for invalid JSON, missing body, invalid enum, or validation errors.
- `409 Conflict` when `ruleName` already exists.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/{id}

Partially updates one rule. Only known fields sent in the body are changed. Unknown fields are ignored; if the request contains only unknown fields, the API rejects it as an empty update.

Request body: partial rule update.

```json
{
  "description": "Updated description",
  "isActive": false,
  "isPhotoOld": true
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/rule-001" \
  -H "Content-Type: application/json" \
  -d '{
    "description": "Updated description",
    "isActive": false
  }'
```

Patch rules:

- Missing fields remain unchanged.
- Explicit `null` clears nullable fields such as `description`, `locationWkt`, `locationGeoJson`, and `isPhotoOld`.
- `sensors` replaces the entire sensors object when sent through this route.
- `sensors` and its sensor value lists cannot be `null`.
- `tenantsInfo` replaces the entire tenantsInfo array when sent.
- `ruleName` can be changed only when the new value is unique.
- `algorithmName` cannot be `null` or empty and cannot contain duplicate or unknown values.
- `minimumResolution` cannot be set to `null`, `0`, or a negative value.
- `maximumResolution` cannot be set to `null`, `0`, or a negative value.
- `isActive` cannot be set to `null`.
- After the patch is applied, the full rule must still pass normal rule validation.

Status codes:

- `200 OK` with the updated rule.
- `400 Bad Request` for empty update, unknown-only update, malformed JSON, invalid enum, invalid field values, or invalid final rule.
- `404 Not Found` when the id does not exist.
- `409 Conflict` when changing `ruleName` to a name used by another rule.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/bulk

Applies the same partial update to multiple rules.

Query parameters:

- `ids`: comma-separated rule ids. Whitespace is trimmed and empty segments are ignored.

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/bulk?ids=rule-001,rule-002,missing-rule" \
  -H "Content-Type: application/json" \
  -d '{
    "isActive": false,
    "isPhotoOld": true
  }'
```

Response:

```json
{
  "successIds": ["rule-001", "rule-002"],
  "failedIds": [
    {
      "id": "missing-rule",
      "reason": "Rule 'missing-rule' was not found."
    }
  ]
}
```

Bulk rules:

- Existing rules are updated independently.
- Missing rules are reported in `failedIds`.
- Setting `ruleName` is allowed only when the request has exactly one id.
- Setting the same `ruleName` on multiple ids is rejected with `409 Conflict`.

Status codes:

- `200 OK` when every item succeeds.
- `207 Multi-Status` when some items succeed and some fail.
- `400 Bad Request` for missing ids, empty update, malformed JSON, or invalid field values.
- `409 Conflict` when trying to set one `ruleName` on multiple rules.
- `422 Unprocessable Entity` when every item fails.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/{id}/activity

Changes only the `isActive` flag for one rule.
The `isActive` field is required; `{}` and `{ "isActive": null }` return `400 Bad Request`.

Request body:

```json
{
  "isActive": true
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/rule-001/activity" \
  -H "Content-Type: application/json" \
  -d '{ "isActive": true }'
```

Status codes:

- `200 OK` with the updated rule.
- `400 Bad Request` for malformed JSON or missing body.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/sensors/add

Adds sensor values to one or more rules without replacing the whole `sensors` object.

Query parameters:

- `ids`: comma-separated rule ids.

Request body:

```json
{
  "sensorName": "camera",
  "values": ["rgb-side", "rgb-main"]
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/sensors/add?ids=rule-001,missing-rule" \
  -H "Content-Type: application/json" \
  -d '{
    "sensorName": "camera",
    "values": ["rgb-side", "rgb-main"]
  }'
```

Behavior:

- If the sensor key does not exist, it is created.
- Existing values are not duplicated.
- Missing rule ids are reported in `failedIds`.
- `updateTime` is updated for each successfully mutated rule.

Before:

```json
{
  "sensors": {
    "camera": ["rgb-main"]
  }
}
```

After adding `["rgb-side", "rgb-main"]`:

```json
{
  "sensors": {
    "camera": ["rgb-main", "rgb-side"]
  }
}
```

Status codes:

- `200 OK` when every item succeeds.
- `207 Multi-Status` when some items succeed and some fail.
- `400 Bad Request` for missing ids, empty `sensorName`, empty values, duplicate request values, malformed JSON, or missing body.
- `422 Unprocessable Entity` when every item fails.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/sensors/remove

Removes sensor values from one or more rules without replacing the whole `sensors` object.

Query parameters:

- `ids`: comma-separated rule ids.

Request body:

```json
{
  "sensorName": "camera",
  "values": ["rgb-backup"]
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/sensors/remove?ids=rule-001" \
  -H "Content-Type: application/json" \
  -d '{
    "sensorName": "camera",
    "values": ["rgb-backup"]
  }'
```

Behavior:

- Removing a value that is not present is a no-op for that rule.
- Removing from a sensor key that does not exist is a no-op for that rule.
- If the last value under a sensor key is removed, the sensor key is removed.
- Missing rule ids are reported in `failedIds`.
- `updateTime` is updated for each successfully processed existing rule.

Before:

```json
{
  "sensors": {
    "camera": ["rgb-main", "rgb-backup"]
  }
}
```

After removing `["rgb-backup"]`:

```json
{
  "sensors": {
    "camera": ["rgb-main"]
  }
}
```

Status codes:

- `200 OK` when every item succeeds.
- `207 Multi-Status` when some items succeed and some fail.
- `400 Bad Request` for missing ids, empty `sensorName`, empty values, duplicate request values, malformed JSON, or missing body.
- `422 Unprocessable Entity` when every item fails.
- `503 Service Unavailable` when Elasticsearch access fails.

### DELETE /rules/{id}

Deletes one rule by `_id`.

Example:

```bash
curl -X DELETE "http://localhost:8080/rules/rule-001"
```

Status codes:

- `204 No Content` when the rule was deleted.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

## Rule Audit

Every v2 rule change appends one record to `Elasticsearch:AuditIndex` (default `rules-audit`), shared by all pipelines:

```json
{
  "pipelineId": "FindAir",
  "ruleId": "6f1c...",
  "changeType": "created | updated | activated | deactivated | deleted",
  "previousState": { },
  "newState": { },
  "changedFields": ["match.sensors"],
  "changedBy": null,
  "changedAt": "2026-10-12T10:00:00+00:00",
  "reason": null,
  "requestId": "<trace id>"
}
```

- The states are full rule snapshots. A creation has no `previousState` and a deletion has no `newState`.
- `changedFields` and `changeType` come from comparing the stored rule before and after, not from what the request sent. `activated` and `deactivated` are used when `isActive` is the only change; any other change is `updated`. An update that changes nothing writes no record.
- `requestId` is the HTTP request's `TraceIdentifier`.
- `changedBy` and `reason` stay null until the API authenticates its callers.
- Records are written after the rule is saved. A failed write logs warning 5039 and does not fail the request.

Different pipelines' `runParams` can give the same field different types, so the audit index should store the states without indexing them:

```json
{ "mappings": { "properties": {
  "previousState": { "type": "object", "enabled": false },
  "newState": { "type": "object", "enabled": false }
} } }
```

## Elasticsearch Behavior

The repository stores each rule in the configured rules index using Elasticsearch's metadata `_id` as the document id. The metadata ID is added to API responses but excluded from `_source`. Duplicate checks use the exact `ruleName.keyword` value.

Writes use `Refresh.WaitFor`, so test and follow-up reads can observe saved changes after the Elasticsearch refresh completes.

## Test Coverage

The route, service, validation, and converter behavior is covered by `tests/rules-api-tests`.

Run:

```bash
dotnet test tests/rules-api-tests/ImagingPipeline.Rules.Api.Tests.csproj
```
