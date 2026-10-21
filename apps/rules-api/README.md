# Rules API

`rules-api` manages rule configuration documents for the imaging pipeline. It exposes REST routes under `/rules` and `/pipelines`, stores documents in Elasticsearch, and returns JSON. Historical auto-run routes under `/auto-runs` prepare overlays from Elastic Provider and publish selected work to the Unified Gateway.

Swagger is available at `/swagger` when the app is running. Health is available at `/health`. The historical processing console is available at `/ui/auto-run`; `/ui` redirects there.

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

Auto-run has two explicit stages: prepare a preview, then start publication. Preparation fetches overlays through Elastic Provider and freezes their normalized payloads. When rules are selected, it also evaluates those saved rules, checks each pipeline's input contract, and freezes rule definitions and target assignments. Editing or deleting a selected rule after preparation does not change that run. Overlay IDs can instead be submitted directly without selecting rules; the gateway then applies its current rules when it consumes the ordinary input messages.

The feature is disabled by default. Jobs and previews are held in memory on **one Rules API instance**. Only one job may prepare or publish at a time; unused ready previews can coexist within the retention and payload limits. Restarting the instance loses all jobs and previews. There is no scheduled execution, restart recovery, or coordination between API replicas.

The console uses the same API routes, supports all three source modes, browses saved rules, shows per-rule quotas and preview shortfalls, and polls run progress. Run links use `/ui/auto-run/runs/{id}`. Date inputs let the operator select the browser timezone or UTC and submit explicit UTC timestamps. The UI and API use the deployment's existing access boundary; this feature adds no login, roles, or per-user job isolation.

In the rule browser, **Select all rules** adds every rule in the selected pipeline across all pages, including inactive rules. Rules already selected retain their edited quotas, and targets from other pipelines remain selected. The operation adds nothing if fetching a page fails or the combined selection would exceed the 100-target limit; select fewer rules individually in that case. Selecting rules prepares the configuration only; it does not start a preview or publish messages.

In **Overlay IDs** mode, direct submission is the default: enter IDs, prepare a preview, then start. No pipeline, rules, or search filters are required. The optional selected-rule mode retains historical matching and per-rule quotas. Switching modes preserves the rule draft, but direct requests do not send hidden targets or filters. Direct previews remain available if the pipeline list cannot be loaded.

### Workflow and routes

| Method and route | Behavior |
| --- | --- |
| `GET /auto-runs/options` | Feature status, publication defaults, effective limits, configured provider timezone, and `singleInstance: true`. |
| `POST /auto-runs/preview` | Accepts a preview request and returns `202 Accepted`, a run summary, and its `Location` URL. No messages are published. |
| `GET /auto-runs` | Lists retained run summaries, newest first. |
| `GET /auto-runs/{id}` | Returns state, counters, target quotas, frozen rules, provider queries, warnings, and errors. |
| `GET /auto-runs/{id}/items?from=0&size=50` | Pages through selected overlays and their assigned pipeline/rule pairs. `size` is 1–200; optional `outcome` filters by `Pending`, `InFlight`, `Confirmed`, `Failed`, or `Uncertain`. |
| `POST /auto-runs/{id}/start` | Starts a ready, unexpired preview. Repeating start for a run that has already started returns its current state without publishing it again. |
| `PATCH /auto-runs/{id}/publication` | Changes batch size and delay during preparation, while ready, or during publication. |
| `POST /auto-runs/{id}/cancel` | Cancels preparation or publication, or discards a ready preview. Already queued messages are not recalled. |

Poll the summary until `state` is `Ready`, inspect the selection and warnings, then call start. States are `Preparing`, `Ready`, `Publishing`, `Completed`, `CompletedWithErrors`, `Failed`, `Cancelled`, and `Expired`. Cancellation is cooperative; an active run may remain `Preparing` or `Publishing` until outstanding work settles.

Invalid requests return `400`; missing or forgotten runs return `404`; a busy worker, full retained-job budget, or invalid state transition returns `409`; preparing while disabled returns `503`. A provider failure after the preview is accepted changes the job to `Failed`, so inspect the job summary rather than relying only on the initial HTTP response.

### Preview requests

`Rules` and `Filters` require 1–100 unique `(pipelineId, ruleId)` targets. `OverlayIds` also accepts omitted or empty `targets` for direct gateway submission. When targets are supplied, IDs reference existing schema-version-2 rules from `/pipelines/{pipelineId}/rules`, and the pipeline must be enabled in the catalog. An explicitly selected inactive rule is allowed. Historical matching with selected targets ignores only `maxPhotoAgeDays`; sensor, registration quality, grid type, resolution, geometry, and `modeSAR` conditions still apply.

`Rules` derives provider queries from the selected rules. Each sensor branch retains its own quality and grid conditions. Queries are paged in round-robin order, and repeated overlay IDs are deduplicated across queries. Optional `source.filters` narrows those searches:

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

`Filters` searches with explicit overlay filters, then evaluates the selected rules locally. Empty or omitted filters search the provider without additional conditions; scan and candidate limits still apply. This example shows every supported filter; replace the illustrative field values with values used by your provider:

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
  "targets": [
    {
      "pipelineId": "FindAir",
      "ruleId": "replace-with-findair-rule-id",
      "minOverlays": 10,
      "maxOverlays": 100
    }
  ],
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

Direct submission still validates and normalizes provider overlays, reports missing/invalid IDs, freezes payloads, and enforces overall quotas, memory/scan limits, expiry, and cancellation. Each selected overlay is published as the ordinary JSON body (`id`, `imageUrl`, `roiFootprint`, `sensorName`, and other mapped fields), with the RabbitMQ `x-updated-fields` array containing `gridType`. It has no historical `kind`, `overlay`, or `targets` wrapper and does not require `HistoricalInput.Enabled` on the gateway.

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

Each selected target requires `maxOverlays`; its `minOverlays` defaults to zero and must not exceed the maximum. The overall minimum defaults to zero. With targets, the overall maximum defaults to the sum of target maxima, capped by `AutoRun:MaxSelectedOverlays`. Without targets, it defaults to the unique requested ID count, capped by the same limit. Direct selection prefers newer photo times, then ordinal overlay ID. A shared overlay counts once toward the overall quota and once toward every rule assigned to it. A single overlay may result in multiple downstream requests through matching rules and their run parameters.

Selection prioritizes overlays that help the most unmet target minimums, then fills available target maxima. Ties prefer newer photo times, then ordinal overlay ID. This is a deterministic greedy selection, not a proof that no other assignment could meet the minimums. Maxima are hard limits; minimum shortfalls are reported per target and overall, and do not prevent starting the available selection. Quotas measure selected overlays, not successful downstream processing.

`counters.scanned` counts processed provider records, including invalid records and duplicates. `observedMatches` counts distinct eligible candidates found before selection; it is not the provider's total hit count. A malformed entity or missing explicit ID is reported as invalid and preparation continues. A failed provider request, malformed page, or invalid continuation fails preparation.

| Setting | Default | Meaning |
| --- | --- | --- |
| `AutoRun:MaxSelectedOverlays` | 100,000 | Maximum unique selected overlays per run; configurable downward. |
| `AutoRun:MaxScannedRecords` | 1,000,000 | Maximum processed source records per preview; configurable downward. |
| `AutoRun:MaxCandidates` | 200,000 | Maximum eligible candidates retained for selection; configurable downward. |
| `AutoRun:PayloadBudgetBytes` | 536,870,912 | Process-wide retained serialized request, rule, query, and overlay byte budget; configurable downward. |
| `AutoRun:ReadyLifetimeMinutes` | 30 | Time from readiness until an unstarted preview expires. |
| `AutoRun:SummaryLifetimeMinutes` | 60 | Retention after a terminal state; payloads are released when the run becomes terminal. |
| `AutoRun:MaxRetainedJobs` | 100 | Maximum retained previews and summaries on the instance. |
| `AutoRun:Provider:MaxResponseBytes` | 16,777,216 | Maximum bytes in one provider response, including streamed responses. |

Reaching a scan or candidate limit can produce a ready preview with `sourceExhausted: false`, a `stoppedReason` of `scanLimit` or `candidateLimit`, and a warning. It does not establish the source's total count or guarantee the newest overlays outside the scanned subset. Exceeding the retained payload budget fails preparation instead of creating a runnable partial preview. `sourceExhausted: true` means the adapter exhausted the supplied IDs or returned continuation chain; it is not a transactional snapshot of Elasticsearch.

The payload budget counts serialized bytes, not total heap usage: object overhead and transient provider page buffers use additional memory. Terminal jobs release overlay payloads immediately; their retained requests, rule snapshots, and queries remain charged until summary expiry.

### Publication and outcomes

`publication` may be omitted to use configured defaults. `batchSize` is 1–100 overlays, and `delayMs` is 0–60,000 milliseconds between batches. The default is 10 overlays and a 1,000 ms delay. A batch waits for its publication outcomes before the next batch; actual throughput also depends on broker latency, `AutoRun:PublisherConcurrency` (default 4), and `AutoRun:PublishTimeoutSeconds` (default 30).

To change a run's publication settings, send:

```http
PATCH /auto-runs/{id}/publication
Content-Type: application/json

{ "batchSize": 20, "delayMs": 500 }
```

During publication, changes apply at the next batch boundary. The summary exposes requested `publication`, effective `activePublication`, and `publicationChangePending`. Changing settings does not change the selected overlays, rule snapshots, or quotas.

Each selected overlay produces one persistent, mandatory RabbitMQ message. Direct Overlay IDs without rules use the frozen ordinary overlay body; requests with selected targets use a historical envelope containing the frozen overlay and assigned rule snapshots. Both paths use broker confirmations and the same publication controls. Item outcomes have these meanings:

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
- `POST /ErisImages/v2.0/ErisImages/Images/TableEntities` for filtered batches.

The first request supplies no provider query flags. Batch responses must contain `entities_list`; the optional `next_page` URL is followed with the **same POST body**, including any continuation query string supplied by the provider. Continuations must remain on the configured scheme, host, and port. Redirects are disabled, repeated continuation URLs fail preparation, and an empty `entities_list` terminates paging. Provider request timeout defaults to 30 seconds.

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

Supply real credentials through deployment secrets, for example the environment variable `AutoRun__Provider__Headers__Authorization`. Header names are case-insensitive; values are preserved. Malformed headers and content/transport-managed headers such as `Content-Type`, `Content-Length`, `Host`, and `Connection` fail startup validation. The adapter continues to set JSON content type itself. Header values are redacted in the provider HTTP client's logs and are not exposed through the Auto-run API or UI. Restart Rules API after changing these settings. Continuation requests remain restricted to the configured origin, and redirects remain disabled.

Request construction follows the supplied working logical-operator example, not the differing Swagger skeleton:

```json
{
  "filter": {
    "logical_operators": {
      "AND": [
        { "match": { "SENSOR": { "type": "IN", "values": ["SENSOR_A"] } } },
        { "range": { "RESOLUTION": { "type": "IN", "values": [{ "gte": 0.3, "lte": 1.2 }] } } }
      ]
    }
  },
  "sort": { "date": "desc" }
}
```

Different rule/sensor branches use separate AND queries. The adapter does not invent an OR grammar or retry by dropping a rejected condition. Provider request aliases are configurable under `AutoRun:Provider:RequestFields`:

| Option | Default request alias |
| --- | --- |
| `Id` | `GlobalId` |
| `SensorName` | `SENSOR` |
| `SensorType` | `IMAGINGTECHNIQUE` |
| `GridType` | `SOLUTIONTYPE` |
| `RegistrationQuality` | `REGISTRATIONQUALITY` |
| `SortieId` | `SORTIE` |
| `IsRealtime` | `ISREALTIME` |
| `CreatedTime` | `CREATED` |
| `PhotoTime` | `PHOTOTIME` |
| `Resolution` | `RESOLUTION` |
| `Geometry` | `geo` |
| `SortDate` | `date` |
| `ModeSar` | Unset; SAR mode is checked locally. |

**Request aliases and numeric/boolean encodings remain deployment assumptions.** The working example confirms `SENSOR`, `geo`, and the logical grammar; it does not verify every alias above. Native JSON numbers and booleans are the defaults. Set `StringEncodedNumbers` or `StringEncodedBooleans` only when the deployed provider requires string values. Public API filters remain native numbers and booleans in either case.

The response contract is separate from request aliases. Both GET and each batch entity contain `properties_list` and `geo.wkt`. These configured response mappings use the supplied example and confirmed provider property names:

| Provider response field | Normalized overlay field |
| --- | --- |
| `properties_list.GlobalId` | `id` |
| `properties_list.Url` | `imageUrl` |
| `properties_list.GridPath` | `gridURI` |
| `properties_list.Sensor` | `sensorName` |
| `properties_list.ImagingTechnique` | `sensorType` |
| `properties_list.SolutionType` | `gridType` |
| `properties_list.Sortie` | `sortieId` |
| `properties_list.Resolution` | `bestResolution` |
| `properties_list.RegistrationQuality` | `registrationQuality` |
| `properties_list.PhotoTime` | `photoTime` |
| `properties_list.Created` | `createdTime` |
| `properties_list.IsRealTime` | `isRealtime` |
| `properties_list.ImageWidth`, `properties_list.ImageHeight` | `width`, `height` |
| `geo.wkt` | Polygon/MultiPolygon GeoJSON `roiFootprint` |

Response aliases can be changed under `AutoRun:Provider:ResponseFields` using the option names in [ElasticProviderOptions.cs](AutoRuns/Provider/ElasticProviderOptions.cs). The GET result's mapped `GlobalId` must equal the requested ID. Response resolution must be a JSON number, and `IsRealTime`, when present, must be a JSON boolean; request encoding flags do not change response parsing.

`SourceTimeZoneId` defaults provisionally to `Asia/Jerusalem`. Confirm the provider's timezone before relying on timezone-less `Created` or `PhotoTime`. Dates carrying explicit offsets honor those offsets. Timezone-less provider dates use the configured timezone, and ambiguous or nonexistent daylight-saving times make that record invalid instead of being guessed. Accepted local date shapes are `dd/MM/yyyy HH:mm:ss` and `yyyy-MM-ddTHH:mm:ss`, optionally with fractional seconds.

Optional `ResponseFields.ModeSar`, `AreaOfInterest`, `LegId`, `PrevOverlayId`, and `NextOverlayId` are unset until their real provider property names are configured. Rules with `modeSAR` always enforce that condition locally; configure its response alias before expecting matches, and configure `RequestFields.ModeSar` separately only if provider filtering is verified. Missing SAR metadata does not silently disable the rule condition.

Shared overlay validity is checked first, then each selected pipeline validates the fields it needs. FindAir checks image URL, dimensions, sensor type, grid URI, and optional area metadata. DeepWisdom checks relationship metadata for `run_every_other_image` when supplied. SarAtc gets `modelName` and `fieldsToFilter` from the saved rule's run parameters, not an invented provider field. An overlay can remain eligible for one target when another target's contract rejects it. See [pipeline contracts](../../libs/pipeline-contracts/README.md) for exact downstream requirements.

### Destination configuration and rollout

The auto-run destination is the **Unified Gateway input queue**, independently of pipeline catalog transport destinations. This example matches the repository's default gateway input topology; replace host and credentials for the deployment:

```json
{
  "AutoRun": {
    "Enabled": false,
    "Publication": { "BatchSize": 10, "DelayMs": 1000 },
    "Provider": {
      "BaseUrl": "https://elastic-provider.example",
      "TableEntitiesPath": "/ErisImages/v2.0/ErisImages/Images/TableEntities",
      "SourceTimeZoneId": "Asia/Jerusalem",
      "TimeoutSeconds": 30,
      "MaxResponseBytes": 16777216,
      "StringEncodedNumbers": false,
      "StringEncodedBooleans": false,
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

1. For runs with selected rules, deploy the gateway version supporting historical envelopes to **every consumer** of the input queue and enable `HistoricalInput__Enabled=true` before publication. Direct Overlay IDs without rules use ordinary input and do not require this flag or historical-envelope support.
2. Deploy one Rules API instance with `AutoRun:Enabled=false`, configure its provider and exact gateway destination, and verify the enabled pipeline IDs and contracts agree with the gateway.
3. Verify the real provider contract in the deployment: field casing, numeric resolution bounds, both boolean values, date ranges/timezone, Polygon and MultiPolygon intersections, required response mappings, and at least two pages followed by termination. Check returned values, not only HTTP success.
4. Enable `AutoRun__Enabled=true`, prepare a small preview, inspect its assigned targets and warnings, then start it. Verify broker confirmations and correlated gateway dispatch before increasing quotas or batch size.

The implementation has local automated coverage; **the real Elastic Provider, RabbitMQ deployment topology, and end-to-end dispatch have not been validated against a live deployment**. A ready preview validates available overlay data and, for selected-rule runs, their pipeline contracts, but does not establish broker connectivity. Scaling this version to multiple Rules API replicas is unsupported.

### Auto-run observability

Preparation and publication emit `auto_run.prepare` and `auto_run.publish` spans with `auto_run.id`, plus correlated logs for state, selected counts, and confirmed/failed/uncertain publications. The Rules API meter exposes `auto_run.selected.overlays` and `auto_run.publications`; the latter has a bounded `outcome` dimension. IDs remain in spans/logs and are not metric labels.

The summary exposes current counts, per-target shortfalls, `confirmedPerSecond`, and bounded warning/error samples. Gateway telemetry identifies historical envelopes through `auto_run.run_id` and `unified_gateway.input_kind`. Direct input follows ordinary gateway telemetry, with the run ID retained in the message correlation ID. Use gateway events to distinguish confirmed enqueueing from later dispatch or downstream failures.

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
