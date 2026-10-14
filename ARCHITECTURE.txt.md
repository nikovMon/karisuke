# Karisuke — Component Reference

Comprehensive, code-grounded documentation for every core component in the monorepo: the five runnable applications under `apps/`, the ten shared libraries under `libs/`, and the `deploy/observability` configuration bundle. Each component is documented with the same five sections: **Definitions & Purpose**, **Execution Flow**, **External Dependencies & Consumed Services**, **Configuration & Environment Variables**, and **Input & Output Message Schemas**.

File references are `path:line` relative to the repository root (`c:\Users\lihik\source\repos\karisuke`). This document is a deep-dive companion to the per-component `README.md` files that already exist for most libraries and `apps/rules-api`, `apps/unified-gateway`; it does not replace them.

## Table of Contents

**Applications**
1. [apps/gateway](#1-appsgateway)
2. [apps/unified-gateway](#2-appsunified-gateway)
3. [apps/rules-api](#3-appsrules-api)
4. [apps/tb-publisher](#4-appstb-publisher)
5. [apps/tb-consumer](#5-appstb-consumer)

**Libraries**
6. [libs/common-dtos](#6-libscommon-dtos)
7. [libs/pipeline-catalog](#7-libspipeline-catalog)
8. [libs/pipeline-contracts](#8-libspipeline-contracts)
9. [libs/rule-engine](#9-libsrule-engine)
10. [libs/elasticsearch-client](#10-libselasticsearch-client)
11. [libs/rabbitmq-client](#11-libsrabbitmq-client)
12. [libs/rabbitmq-configuration](#12-libsrabbitmq-configuration)
13. [libs/projection-mapper-client](#13-libsprojection-mapper-client)
14. [libs/geometry-utils](#14-libsgeometry-utils)
15. [libs/observability](#15-libsobservability)

**Deployment**
16. [deploy/observability](#16-deployobservability)

---

## 1. `apps/gateway`

### Definitions & Purpose

`ImagingPipeline.Gateway` is a .NET 10 worker hosted in an ASP.NET Core minimal host (`Program.cs:12`) — not a business HTTP API. It is the **legacy, single-pipeline** RabbitMQ consumer: it matches incoming image-update messages against active geospatial rules (loaded from Elasticsearch) and fans each match out into one output task message per matched-rule × tenant, hard-wired to the FindAir/RPN algorithm family (`AlgorithmName` enum: `FindAir`, `Rpn`). `apps/unified-gateway` is its multi-pipeline successor (FindAir **and** DeepWisdom) with per-pipeline transport configuration; the root README frames `gateway` as the predecessor. It exposes no business HTTP routes — only a Prometheus `/metrics` scrape endpoint.

### Execution Flow

1. `Program.Main` validates the `RegistrationQuality` enum contract, builds the host, wires OpenTelemetry, and binds/validates `GatewaySettings` with `ValidateOnStart`.
2. Registers the Elasticsearch client, RabbitMQ client, and two hosted services: `ActiveRuleCache` and `GatewayWorker`; maps the `/metrics` endpoint.
3. `ActiveRuleCache.StartAsync` loads rules once synchronously (blocking host startup) via `IRuleRepository`, then refreshes in the background every `RuleRefreshIntervalSeconds` + random jitter up to `RuleRefreshJitterSeconds`. A failed refresh keeps the last good snapshot; an all-rejected non-empty load throws `InvalidDataException`.
4. `GatewayWorker.ExecuteAsync` runs `IRabbitMqConsumer.ConsumeAsync` in a retry loop with backoff on failure.
5. Per message, `HandleAsync`:
   - Skips messages whose `x-updated-fields` AMQP header lacks `gridType`.
   - Parses the JSON body via `GatewayInputMessageParser.Parse` into a `GatewayInputMessage`.
   - Runs `RuleMatcher.Match` against the current `ActiveRuleCache` snapshot — filtering by photo age, sensor/registration-quality/grid-type, resolution range, and geometry intersection.
   - Builds one `GatewayOutputMessage` per (matched rule × tenant) via `GatewayOutputMessageBuilder.BuildOutputs`.
   - Wraps each as a new `RabbitMqMessageEnvelope` derived from the input envelope and returns `RabbitMqMessageProcessingResult.Success(outputMessages)` for the RabbitMQ client to publish/ack.
6. Error handling: `GatewayValidationException` → non-retryable rejection (logged, no retry); `GatewayProcessingException`/`GatewayDependencyException` (e.g. Elasticsearch failure) → retryable failure; `OperationCanceledException` propagates on shutdown; any other exception is rethrown and treated as retryable by the outer consume loop.

### External Dependencies & Consumed Services

- **RabbitMQ** (`libs/rabbitmq-client`) — consumes `RabbitMq:InputQueue` (`gateway.input.q`), publishes to `OutputQueue` (`int.algo.tb_publisher`), with dead-letter queue, retry exchange/queues, and configurable prefetch/concurrency.
- **Elasticsearch** (`libs/elasticsearch-client`) — paginated point-in-time queries against `Elasticsearch:Index` filtered on `isActive: true` for active rules.
- **NuGet**: `NetTopologySuite` for geometry intersection math.
- **Internal libs**: `common-dtos`, `elasticsearch-client`, `geometry-utils`, `observability`, `rabbitmq-client`.
- No outbound HTTP calls to other services.

### Configuration & Environment Variables

`Gateway` section (`Configuration/GatewaySettings.cs`):

| Key | Type | Default | Constraint |
|---|---|---|---|
| `ShutdownTimeoutSeconds` | int | 30 | > 0 |
| `RuleRefreshIntervalSeconds` | int | 60 | > 0 |
| `RuleRefreshJitterSeconds` | int | 0 | ≥ 0 |
| `MaxPhotoAgeDays` | int | 30 | > 0, within `TimeSpan` range |

`RabbitMq` section: `Host`, `Port`, `Username`, `Password`, `VirtualHost`, `InputCluster:{Host,Port,Username,Password,VirtualHost}`, `InputQueue`, `OutputQueue`, `OutputQueueHeaders`, `DeadLetterQueue`, `RetryExchange`, `RetryExchangeType`, `RetryRoutingKey`, `PrefetchCount`, `ConsumerConcurrency`, `PublisherChannelPoolSize`, `OutputPublishConcurrency`, `MaxRetryAttempts`, `RetryQueues[]` (`RetryCount`, `Queue`, `DelayMilliseconds`, `BindingArguments`), `ReconnectDelaySeconds`.

`Elasticsearch` section: `Uri`, `Index`, `TimeoutSeconds`.

`Observability:*` / `Logging:*` — standard shared contract (see §15). Standard OTel env vars: `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`, `OTEL_TRACES_SAMPLER`, `OTEL_TRACES_SAMPLER_ARG`. Prometheus endpoint: `Observability:Metrics:Prometheus:{Path,Port}` (`/metrics`, `9464`).

### Input & Output Message Schemas

**Input** — `GatewayInputMessageDto` (JSON body on `InputQueue`):

```json
{
  "id": "string",
  "sensorName": "string",
  "sensorType": "string",
  "registrationQuality": "Accurate | Sensor",
  "bestResolution": 0.0,
  "areaOfInterest": "string | null",
  "imageUrl": "string",
  "width": 0,
  "height": 0,
  "photoTime": "2026-01-01T00:00:00Z",
  "roiFootprint": { "...": "GeoJSON geometry" },
  "gridType": "string",
  "gridURI": "string"
}
```

Required AMQP header: `x-updated-fields` must include `"gridType"`.

**Output** — one `GatewayOutputMessage` per matched rule × tenant, serialized as `GatewayOutputMessageDto`:

```json
{
  "taskId": "string",
  "ruleId": "string",
  "algorithmName": ["FindAir" ],
  "tenantId": "string",
  "tilingConfigs": [
    { "tileSizeWidth": 0, "tileSizeHeight": 0, "tileOverlapWidth": 0, "tileOverlapHeight": 0 }
  ],
  "imageId": "string",
  "roiFootprint": { "...": "GeoJSON — rule ∩ image intersection" },
  "photoTime": "2026-01-01T00:00:00Z",
  "sensorType": "string",
  "imageUrl": "string",
  "imageWidth": 0,
  "imageHeight": 0,
  "bestResolution": 0.0,
  "sensorName": "string",
  "areaOfInterest": "string | null",
  "gridType": "string",
  "gridURI": "string"
}
```

---

## 2. `apps/unified-gateway`

### Definitions & Purpose

Unified Gateway consumes image updates, matches independently-refreshed pipeline rules, prepares one dispatch unit per matched run, and dispatches through RabbitMQ or HTTP. It supports two pipeline contracts simultaneously: **FindAir** and **DeepWisdom**. It is an ASP.NET Core minimal-API host whose `Main` wires the pipeline catalog, pipeline contracts, rule cache, dispatcher, and a RabbitMQ consumer. It does **not** own pipeline/rule CRUD or metadata management — that is `apps/rules-api`'s responsibility; unified-gateway only *reads* rules through `IRuleRepository`/Elasticsearch. `/health` reports host capabilities only, not broker or Elasticsearch connectivity, and exposes no pipeline-metadata routes. See `apps/unified-gateway/README.md` for the authoritative deep-dive this summary is built from.

### Execution Flow

1. **Startup** — `GatewayApplicationBuilder.Create` layers the shared `pipelinecatalog.json` as a configuration base. `GatewayRuleCache.StartAsync` loads every enabled pipeline's rules before the host is considered started; any failure blocks startup. Hosted services are ordered so rules load before the RabbitMQ consumer starts.
2. **Rule refresh** — `GatewayRuleCache.ExecuteAsync` loops on `RuleRefresh:IntervalSeconds` + random jitter up to `RuleRefresh:JitterSeconds`. Per-pipeline refresh failures (or an all-rejected nonempty load) retain the previous snapshot rather than going empty.
3. **Consume** — `SourceMessageHandler.HandleAsync` only proceeds when the AMQP header `x-updated-fields` contains `gridType`; otherwise the message is filtered with reason `grid_type_not_updated`.
4. **Parse** — `GatewayInputMessageParser.Parse(message.Body)` builds a `GatewayInputMessage`; a `PipelineDispatchContext` overlay is built from the same raw JSON. Parse failure → non-retryable failure.
5. **Rule match** — for each enabled pipeline, `RuleMatcher.Match(image, pipelineRules.Rules)` returns matches; no matches → `no_matching_rules`; no enabled pipelines at all → `no_enabled_pipelines`.
6. **Dispatch ID / dedupe** — for each matched rule's `RunParams`, `DispatchIdentity.Create(imageId, pipelineId, ruleId, runParams)` produces a 64-hex SHA-256 ID over canonicalized JSON (sorted object keys, preserved array order); collisions are filtered as `duplicate_run`.
7. **Prepare** — `PipelineWorkPreparer.Prepare` validates run params/extra data/context via the pipeline contract and calls `contract.BuildPayload(...)`. Invalid/disabled pipelines are skipped and the pipeline is marked invalid rather than failing the whole message.
8. **Dispatch** — `PipelineDispatcher.DispatchAsync` fans out all dispatch units in parallel (`Task.WhenAll`), routing each by `TransportKind` to a RabbitMQ or HTTP transport. Transport exceptions are caught and reported as `Retryable` outcomes — never thrown directly.
9. **Decide** — any retryable outcome → the whole source message is retried; any rejected/invalid pipeline (with no retryable units) → dead-letter; otherwise success. A retryable unit retries the *entire* original source message, so successful siblings can be delivered again under the same dispatch ID — downstream receivers must deduplicate by dispatch ID.

### External Dependencies & Consumed Services

- **RabbitMQ** — source consumption from `RabbitMq:InputQueue` (`unified-gateway.input.q`), dead-letter to `unified-gateway.dlq.q`, retries via `unified-gateway.retry.{1,2,3}.q` bound to a headers exchange `pipeline.retry.x`. Output published per-pipeline to the catalog-declared RabbitMQ destination (e.g. FindAir → `int.algo.tb_publisher` on connection `findair-output`).
- **Elasticsearch** — `ElasticsearchRuleRepository : IRuleRepository` reads active rules per pipeline's configured `RulesIndex` (e.g. `asd-integ-pipeline-index`, `algo-integ-pipeline-index`).
- **FindAir** — RabbitMQ transport target.
- **DeepWisdom** — HTTP transport target, `PUT` to a configured endpoint with a 20s timeout.
- **Internal libs**: `pipeline-catalog`, `pipeline-contracts`, `observability`, `rule-engine`, `elasticsearch-client`, `rabbitmq-client`.
- No direct call to `rules-api` and no Projection Mapper usage.

### Configuration & Environment Variables

- `Observability:*` (traces/metrics/Prometheus port `9464`/logs; `Logs:DataStream:Dataset = unified_gateway`).
- `Elasticsearch:{Uri,Username,Password,TimeoutSeconds}`.
- `RuleRefresh:{IntervalSeconds (default 60), JitterSeconds (default 5)}`.
- `RabbitMq:{Host,Port,Username,Password,VirtualHost,InputQueue,DeadLetterQueue,RetryExchange,RetryExchangeType,RetryRoutingKey,PrefetchCount,ConsumerConcurrency,PublisherChannelPoolSize,MaxRetryAttempts,RetryQueues[],ReconnectDelaySeconds}`.
- `PipelineCatalogFile` — overrides the entire catalog JSON path.
- `PipelineCatalog:RabbitMqConnections:<name>:{Hostname,Port,Username,Password,VirtualHost}` and `PipelineCatalog:Pipelines:<id>:{Enabled,ContractId,RulesIndex,ExtraData,Transport:{Kind,RabbitMq:{...},Http:{...}}}` — see §7 for the full catalog schema. `ExtraData` overrides must be supplied as a complete JSON string, e.g. `PipelineCatalog__Pipelines__FindAir__ExtraData={"custom":true}`.
- `Logging:LogLevel:ImagingPipeline.UnifiedGateway.Source.SourceMessageHandler=Debug` enables the filter-reason debug event (6022).

### Input & Output Message Schemas

**Input** — `GatewayInputMessageDto` (identical shape to §1's input schema). Required AMQP header `x-updated-fields` must list `gridType`.

**FindAir dispatch payload** (built by `FindAirPipelineContract.BuildPayload`):

```json
{
  "taskId": "string", "ruleId": "string", "algorithmName": ["FindAir"],
  "tenantId": "string",
  "tilingConfigs": [ { "tileSizeWidth": 0, "tileSizeHeight": 0, "tileOverlapWidth": 0, "tileOverlapHeight": 0 } ],
  "imageId": "string", "roiFootprint": {}, "photoTime": "...", "sensorType": "string",
  "imageUrl": "string", "imageWidth": 0, "imageHeight": 0, "bestResolution": 0.0,
  "sensorName": "string", "areaOfInterest": "string|null", "gridType": "string", "gridURI": "string",
  "extraData": {}
}
```
AMQP attribute `findair-contract-version = 1` is attached to the message.

**DeepWisdom dispatch payload** (built by `DeepWisdomPipelineContract.BuildPayload`), sent as an HTTP `PUT` body:

```json
{
  "modelName": "string", "focusedWkt": "string", "origin": "string|null",
  "queueType": "string|null", "priority": 0, "saveDetections": true,
  "algorithmName": "string", "missionName": "string", "username": "string",
  "displayName": "string", "requestingUnit": "string",
  "runEveryOtherImage": false, "shouldCheckInVip": false,
  "tasksData": [ { "imageId": "string", "legId": "string", "prevOverlayId": "string", "nextOverlayId": "string", "photoTime": "..." } ],
  "extraData": {}
}
```

---

## 3. `apps/rules-api`

### Definitions & Purpose

`ImagingPipeline.Rules.Api` manages Elasticsearch-backed rule configuration documents for the imaging pipeline and exposes the pipeline catalog as a read-only API. It owns two rule models: legacy v1 rules (`RuleDto`) under `/rules`, and v2 per-pipeline rules (`PipelineRuleDocument`) under `/pipelines/{pipelineId}/rules`, plus a read-only `/pipelines` catalog view. Per its README, rule-management and pipeline-metadata routes belong here, not in unified-gateway.

### Execution Flow

**v1 `POST /rules`** (`RulesController.Create`) → `RuleService.CreateAsync`: maps `CreateRuleRequest` → `RuleDto`, runs `RuleValidation.ValidateRule`, converts `locationWkt` → GeoJSON, checks name uniqueness, stamps `creationTime`/`updateTime`, normalizes sensors, saves via `RuleIndex`/`IElasticsearchDocumentClient`, returns `201`/`400`/`409`/`503`.

**v2 `POST /pipelines/{pipelineId}/rules`** — `KnownPipelineFilter` 404s unknown pipelines before the action runs → `PipelineRuleService.CreateAsync`: builds a `PipelineRuleDocument`, assigns a GUID id and timestamps, validates via `PipelineRuleValidator.Validate(rule, contract)` from `libs/rule-engine` against the pipeline's own contract (the same validator unified-gateway uses), derives `match.location` from `match.locationWkt`, checks in-pipeline name uniqueness, saves with `waitForRefresh: true`, and **writes an audit record** via `RuleAuditWriter.RecordAsync`.

**Audit-every-v2-change behavior** — every create/update/activate/deactivate/delete on a v2 rule appends one `RuleAuditRecord` to the `Elasticsearch:AuditIndex` (default `rules-audit`). `UpdateRuleAsync` computes `changedFields`/`ChangeType` by diffing before/after JSON snapshots (not the raw request payload); no-op updates write no record; audit writes use `waitForRefresh: false`; a failed audit write logs a warning but never fails the originating request.

Bulk and sensor-mutation routes (`PATCH .../bulk`, `.../sensors/add|remove`) loop per-id, validating/saving each rule independently, aggregating a `BulkOperationResult` with `200`/`207`/`422` outcomes. All actions are wrapped by `RulesOperationFilter` (spans/metrics); global exception handling maps `RulePersistenceException` → `503`.

### External Dependencies & Consumed Services

- **Elasticsearch** — primary datastore via NEST/`Elasticsearch.Net` through `libs/elasticsearch-client`; health-checked via `IElasticClient.PingAsync`.
- **Internal libs**: `elasticsearch-client`, `common-dtos`, `observability`, `pipeline-catalog`, `pipeline-contracts`, `rule-engine`.
- **No RabbitMQ consumption** — RabbitMQ credentials only need placeholders for config validation; transport isn't used.
- **OpenTelemetry Collector** (OTLP traces), **Prometheus** (`/metrics`), **Logstash** (ECS JSON logs over HTTP) via `libs/observability`.
- NuGet: `Microsoft.OpenApi`, `NetTopologySuite`/`NetTopologySuite.IO.GeoJSON` (WKT ↔ GeoJSON), `Swashbuckle.AspNetCore`.

### Configuration & Environment Variables

`Elasticsearch` section: `Uri` (default `http://localhost:9200`), `Index` (`rules`), `AuditIndex` (`rules-audit`), `Username`/`Password` (env: `Elasticsearch__Username`/`Elasticsearch__Password`, both required together if auth enabled), `TimeoutSeconds` (30), `DefaultSearchSize` (100), `MaxSearchSize` (1000), `MaxBulkSize` (500).

`PipelineCatalogFile` names the catalog file. `Observability:{Enabled,ServiceNamespace,DeploymentEnvironment,Traces:{Enabled,RecordExceptions,ExcludeHealthChecks,OtlpEnabled,DefaultSamplingRatio},Metrics:Prometheus:{Path,Port},Logs:Logstash:{Enabled,Endpoint},Logs:DataStream:{Dataset,Namespace}}`.

`Logging__LogLevel__ImagingPipeline.Rules.Api=Debug` for verbose logs. Docker: `ASPNETCORE_URLS=http://+:8080`, port `8080` exposed (and mapped to the host in `docker compose up`).

### Input & Output Message Schemas

- `GET/POST /rules`, `GET /rules/{id}`, `GET /rules/name/{ruleName}` → `RuleDto`: `_id`, `ruleName`, `description?`, `algorithmName: AlgorithmName[]`, `sensors: SensorConfig[]`, `isActive`, `tenantsInfo: TenantInfo[]`, `minimumResolution`, `maximumResolution`, `area`, `locationWkt`, `locationGeoJson?`, `isPhotoOld?`, `creationTime`, `updateTime`. `POST` body = `CreateRuleRequest` (same shape minus `_id`/timestamps).
- `PATCH /rules/{id}`, `/bulk`, `/{id}/activity`, `/sensors/add|remove` bodies: `UpdateRuleRequest` (partial, field-presence tracked) / `ChangeRuleActivationStatusRequest{isActive}` / `RuleSensorUpdateRequest{sensorName, values}`.
- `GET/POST /pipelines/{pipelineId}/rules...` → `PipelineRuleDocument`:

```json
{
  "_id": "string", "schemaVersion": 2, "ruleName": "string", "description": "string|null",
  "isActive": true, "matchAll": false,
  "match": {
    "sensors": [ { "name": "string", "registrationQualities": ["Accurate"], "gridTypes": ["string"] } ],
    "resolution": { "min": 0.0, "max": 0.0 },
    "locationWkt": "string|null", "location": {}, "maxPhotoAgeDays": 0
  },
  "runParams": [ {} ],
  "createdBy": "string|null", "updatedBy": "string|null",
  "creationTime": "...", "updateTime": "..."
}
```
`POST` body = `CreatePipelineRuleRequest{ruleName, description?, isActive, matchAll, match?, runParams}`.
- `GET /pipelines[/{id}]` → `PipelineResponse{pipelineId, contractId, enabled}`.
- Bulk responses → `BulkOperationResult{successIds: string[], failedIds: [{id, reason}]}`.
- Audit record (internal, written to `rules-audit`, not exposed via API) → `RuleAuditRecord{pipelineId, ruleId, changeType: created|updated|activated|deactivated|deleted, previousState?, newState?, changedFields?, changedBy: null, changedAt, reason: null, requestId}`.
- `GET /health` → `{"status": "Healthy"|"Unhealthy"}` (200/503). Errors → `{"error": "..."}`.

---

## 4. `apps/tb-publisher`

### Definitions & Purpose

TB Publisher is a .NET 10 worker service that consumes gateway-produced task messages from RabbitMQ, resolves geometry through the external Projection Mapper HTTP service, and republishes one Tile Builder request DTO per tiling configuration toward the integration boundary that stands in for the external Tile Builder service (not present in this repository). Per the root README, the external Tile Builder forwards W3C trace/baggage and timing headers while exporting its own telemetry separately; TB Publisher is the producer side of that boundary, and `tb-consumer` is the counterpart on the way back.

### Execution Flow

1. `Program.cs` builds the host, wires observability and a Prometheus listener, registers `AddRabbitMqConsumer` and `AddProjectionMapperClient`, DI singletons for input validation, geometry conversion, output message building, and a `IRabbitMqMessageHandler` → `TbPublisherMessageHandler`, and starts `Worker` as a `BackgroundService`.
2. `Worker` loops calling `IRabbitMqConsumer.ConsumeAsync`; on unexpected exit/exception it backs off via `RabbitMqConsumerRestartBackoff` (seeded from `RabbitMq:ReconnectDelaySeconds`) and retries, recording a consumer-restart metric.
3. **Identity resolution** — `DeterministicIdGenerator.CreateMissionId(messageId)` hashes `"{taskId}:mission"` with SHA-256, forces RFC-4122-style version/variant bits, and returns a deterministic GUID so the same `taskId` always maps to the same `missionId`.
4. **Validation** — the raw body deserializes into `GatewayOutputMessageDto` and runs DataAnnotations validation; failures reject the message.
5. **Geometry processing** — `TbPublisherGeometryConverter.ExtractCoordinates` parses the GeoJSON `roiFootprint` into `[X,Y]` pairs; `BuildFocusedPxWkt` converts projected pixel coordinates into a polygon WKT. Both wrap failures as `TbPublisherValidationException` (terminal, non-retryable).
6. **Projection mapping** — builds a `ProjectionMapperRequestDto{Coordinates}` and calls the Projection Mapper client's ground-to-image endpoint. Dependency failures return a retryable failure.
7. **Output construction** — for each `TilingConfig` on the input, builds a `TbPublisherOutputMessageDto` with scaled tile dimensions (`tileSize/Overlap * 100 / BestResolution`), the focused WKT, mission metadata (deterministic `missionId`), and a fresh `RequestId`.
8. **Publish** — serializes each output DTO, forwards trace headers (`traceparent`, `tracestate`, timing header) via `FindAirMessageHeaders.Forward`, wraps in a new envelope keyed `"{message.MessageId}-{index}"`, and publishes to the output queue. If publish fails mid-loop after some messages already succeeded, the handler explicitly logs that already-published outputs may be duplicated when RabbitMQ retries the input.
9. **Errors** — `TbPublisherProcessingException` (abstract base) and `TbPublisherValidationException` represent terminal geometry faults; any other exception propagates and triggers RabbitMQ-level retry via the configured retry/DLQ topology.

### External Dependencies & Consumed Services

- **RabbitMQ**: `InputQueue = int.algo.tb_publisher`, `OutputQueue = int.algo.tb_publisher.tiling_config`, `DeadLetterQueue = int.algo.tb_publisher.dlq`, `RetryExchange = int.algo.tb_publisher.retry.x` (type `headers`), `RetryRoutingKey = int.algo.tb_publisher.retry`, retry queues `.retry.1/.2/.3` (10s delay each), `MaxRetryAttempts = 3`.
- **Projection Mapper** (external HTTP service, distinct from Tile Builder): configured via the `ProjectionMapper` section (host, endpoints `G2IMultiPoints`/`I2GById`/`I2GByRegistration`, `SendingSystem=flare`).
- **Tile Builder boundary**: no direct in-repo dependency — the `OutputQueue` message *is* the DTO contract representing that boundary.
- **Internal libs**: `rabbitmq-client`, `projection-mapper-client`, `common-dtos`, `geometry-utils`, `observability`.

### Configuration & Environment Variables

`Observability:*` (`ServiceNamespace=findair`, `Traces:*`, `Metrics:Prometheus:{Path=/metrics,Port=9464}`, `Logs:{Logstash:{Enabled,Endpoint}, DataStream:{Dataset,Namespace}}`); `Logging:LogLevel:*`; `RabbitMq:{Host,Port,Username,Password,VirtualHost,InputQueue,OutputQueue,DeadLetterQueue,RetryExchange,RetryExchangeType,RetryRoutingKey,MaxRetryAttempts,RetryQueues[],PrefetchCount,ConsumerConcurrency,PublisherChannelPoolSize,OutputPublishConcurrency,ReconnectDelaySeconds}`; `ProjectionMapper:{Host,Endpoints:{G2IMultiPoints,I2GById,I2GByRegistration},SendingSystem,UseCache,TimeoutSeconds}`. Dockerfile exposes port `9464` for Prometheus scraping; no env vars are baked into the image.

### Input & Output Message Schemas

**Input** — `GatewayOutputMessageDto` (same shape as §1/§2's gateway output).

**Output** — `TbPublisherOutputMessageDto`, one per tiling config:

```json
{
  "frameMetadata": { "general": { "imageFileURI": "string", "id": "string" } },
  "modelMetadata": { "overlap_height": 0, "tb_crop_size_y": 0, "overlap_width": 0, "tb_crop_size_x": 0 },
  "focusedPxWkt": "POLYGON(...)",
  "missionMetadata": {
    "missionId": "<deterministic GUID derived from taskId>",
    "tenantId": "string",
    "overlay": {
      "image_id": "string", "image_url": "string", "ruleId": "string", "best_resolution": 0.0,
      "area_of_interest": "string", "algorithm_name": ["FindAir"], "image_width": 0, "image_height": 0,
      "roifootprint": {}, "image_time": "...", "sensor_name": "string", "sensor_type": "string",
      "grid_type": "string", "grid_uri": "string"
    }
  },
  "requestId": "<new GUID, N-format>",
  "taskId": "string"
}
```
`overlap_height`/`tb_crop_size_y`/`overlap_width`/`tb_crop_size_x` are computed as `tilingConfig.* * 100 / message.BestResolution`.

---

## 5. `apps/tb-consumer`

### Definitions & Purpose

TB Consumer is a hosted-service worker that reads tile batches produced by the external Tile Builder off RabbitMQ, maps their pixel ROIs to geographic coordinates via the Projection Mapper, and republishes one message per tile to the Embedder queue. It is a Prometheus-backed minimal host whose only HTTP surface is the metrics scrape endpoint. Since the external Tile Builder exports its own telemetry to a separate backend, TB Consumer continues the inbound W3C trace identity and reports the otherwise-invisible Tile Builder transit as a bounded `tile_builder` external-stage metric.

### Execution Flow

1. `Program.Main` wires observability, a Prometheus listener, a RabbitMQ consumer (which also registers `IRabbitMqPublisher`), a Projection Mapper HTTP client, and the `Worker` hosted service.
2. `Worker.ExecuteAsync` loops calling `ConsumeAsync`; on unexpected exit/failure it records a consumer-restart metric and backs off via `RabbitMqConsumerRestartBackoff`.
3. **Trace continuation & transit metric** — on first-attempt, non-redelivered consumption, the RabbitMQ client layer reads the broker-forwarded publish timestamp and, because `RabbitMq:ForwardedInputStage = "TileBuilder"`, records an external-stage-duration metric tagged `pipeline.stage=tile_builder` (metric name `findair.external_stage.duration`).
4. **Handler entry** — `TbMessageHandler.HandleAsync` opens a pipeline-stage telemetry scope, deserializes the body into `TbConsumerInputDto`, builds a telemetry log context, and attaches pipeline context to the current `Activity` — the W3C `traceparent`/`tracestate` were already extracted/propagated by the RabbitMQ client layer, so this is the "continue the same trace identity" step.
5. **Validation** — enforces non-empty `requestId`, `metadata`, `taskId`, `missionMetadata`, `overlay`, required business fields (`tenantId`, `imageId`, `ruleId`, `sensorName`), a non-empty tile list, consistent `tilesAmount`/`batchTilesAmount`, unique/in-range tile indexes, finite 4-value ROI + non-empty URI per tile, and ≥1 unique valid `AlgorithmName`. Validation failures are classified `Rejected` (not retried).
6. **Header forwarding** — `FindAirMessageHeaders.Forward` copies `traceparent`, `tracestate`, `findair-started-at-unix-ms`, normalizes `algorithmName`/`tenantId`, and stamps `findair-contract-version`.
7. **Projection mapping** — builds 4 ROI corners per tile and calls the Projection Mapper client's registration endpoint if `GridType == "MSP"` and `ImageId` starts with `SHR`, otherwise the plain batch endpoint. A count mismatch or exception is classified `Retryable`/`Dependency` (message redelivered).
8. **Publish to Embedder** — `EmbedderInputMessageBuilder.Build` constructs one `EmbedderInputDto` per tile; the batch span's trace context is injected onto outgoing headers (continuing trace identity downstream); each message is published via `IRabbitMqPublisher.PublishToOutputAsync` inside a suppressed-instrumentation scope. Publish failure is classified `Retryable`/`Publish` and rethrown — unacknowledged input is redelivered, so already-published tiles may duplicate.
9. **Success path** — records tile/coordinate batch sizes and end-to-end duration, then marks the stage succeeded.
10. **Error handling** — unhandled exceptions mark the stage faulted (classified `Retry`/`Handler`) before rethrow; `OperationCanceledException` is classified `Cancelled`. RabbitMQ retry queues redeliver with incrementing `retry-count` up to `MaxRetryAttempts`.

### External Dependencies & Consumed Services

- **RabbitMQ**: `InputQueue = int.algo.tile_builder.output`, `OutputQueue = int.algo.embedder.input`, `DeadLetterQueue = int.algo.tile_builder.dlq`, `RetryExchange = tb-consumer.retry.x` (type `headers`), `RetryRoutingKey = tb-consumer.retry`, retry queues `tb-consumer.retry.1.q/2.q/3.q`.
- **Projection Mapper** — HTTP client for pixel→ground coordinate conversion.
- **External Tile Builder** — not in-repo; produces the `int.algo.tile_builder.output` queue content; observable only via the `tile_builder` external-stage metric.
- **Embedder** — downstream consumer of `int.algo.embedder.input`; not in-repo.
- **Internal libs**: `common-dtos`, `projection-mapper-client`, `observability`, `rabbitmq-client`.

### Configuration & Environment Variables

`Observability:*` (`ServiceNamespace=findair`, `Traces:*`, `Metrics:Prometheus:{Enabled,Path=/metrics,Port=9464}`, `Logs:{Enabled,ConsoleEnabled,Logstash:{Enabled,Endpoint},DataStream:{Dataset,Namespace}}`); `Logging:LogLevel:*`, `Logging:EcsHttp:LogLevel:*`; `RabbitMq:{Host,Port,Username,Password,VirtualHost,InputQueue,OutputQueue,DeadLetterQueue,RetryExchange,RetryExchangeType,RetryRoutingKey,PrefetchCount,MaxRetryAttempts,ForwardedInputStage ("TileBuilder" — drives the transit metric),RetryQueues[],ConsumerConcurrency,PublisherChannelPoolSize,OutputPublishConcurrency,ReconnectDelaySeconds}`; `ProjectionMapper:{Host,SendingSystem,TimeoutSeconds,Endpoints:{G2IMultiPoints,I2GById,I2GByRegistration}}`. Dockerfile exposes port `9464` only; no explicit `ENV` lines — configuration is entirely via `appsettings.json` and standard ASP.NET env-var override convention.

### Input & Output Message Schemas

**Input** — `TbConsumerInputDto`:

```json
{
  "requestId": "string", "tilesAmount": 0, "batchTilesAmount": 0,
  "metadata": {
    "frameMetadata": {}, "modelMetadata": { "overlap_height": 0, "tb_crop_size_y": 0, "overlap_width": 0, "tb_crop_size_x": 0 },
    "focusedPxWkt": "string",
    "missionMetadata": { "missionId": "string", "tenantId": "string", "overlay": { "...": "OverlayDto, see below" } },
    "requestId": "string", "taskId": "string"
  },
  "tileUniqueMetadata": [ { "roi": [0.0, 0.0, 0.0, 0.0], "uri": "string", "tileIndex": 0 } ]
}
```
`OverlayDto`: `image_id`, `image_url`, `ruleId`, `best_resolution`, `area_of_interest`, `algorithm_name` (enum list), `image_width`/`image_height`, `roifootprint` (raw JSON), `image_time`, `sensor_name`, `sensor_type`, `grid_type`, `grid_uri`.

**Output** — one `EmbedderInputDto` per tile:

```json
{
  "frameMetadata": {}, "taskId": "string", "url": "<tile URI>", "s3Uri": "s3://...",
  "embedder_input": {
    "tile_id": "{taskId}_{roiX0}_{roiY1}_{pixelSize}",
    "gid": "string", "image_path": "string", "sensor": "string", "imaging_time": "...",
    "lat": 0.0, "lon": 0.0, "resolution": 0.0, "tiles_size_meters": 0.0,
    "tenant_id": "string", "request_time": "...",
    "tile_coordinates": { "coordinates": [[0.0,0.0],[0.0,0.0],[0.0,0.0],[0.0,0.0]] },
    "algorithms": ["FindAir"], "pixel_roi": [0.0, 0.0, 0.0, 0.0]
  }
}
```

---

## 6. `libs/common-dtos`

### Definitions & Purpose

`ImagingPipeline.Common.Dtos` holds the DTO/model contracts shared across multiple services: gateway input/output envelopes, tb-publisher/tb-consumer messaging DTOs, and the v2 rule document model plus Rules API request/response contracts. It has no `ProjectReference`s of its own — it is a leaf DTO library consumed by `apps/gateway`, `apps/rules-api`, `apps/tb-consumer`, `apps/tb-publisher`, and libs `pipeline-contracts` and `rule-engine`.

### Execution Flow

The gateway deserializes an upstream event into `GatewayInputMessageDto`, matches it against rules, and emits `GatewayOutputMessageDto`, which tb-publisher consumes to build `TbPublisherOutputMessageDto` for tb-consumer. Rules API reads/writes `PipelineRuleDocument` to/from Elasticsearch via the `Rules/Requests` and `Rules/Responses` contracts. This library performs no I/O itself — it's purely data shape.

### External Dependencies & Consumed Services

None beyond the BCL (`System.Text.Json`, `System.ComponentModel.DataAnnotations`).

### Configuration & Environment Variables

None — pure POCOs/records with no options classes.

### Input & Output Message Schemas

```csharp
public sealed class GatewayInputMessageDto {
  string Id; string SensorName; string SensorType; string RegistrationQuality;
  double BestResolution; string? AreaOfInterest; string ImageUrl;
  int ImageWidth /* JSON "width" */; int ImageHeight /* JSON "height" */;
  DateTimeOffset PhotoTime; JsonElement RoiFootprint;
  string GridType; string GridUri /* JSON "gridURI" */;
}

public sealed class GatewayOutputMessageDto : IValidatableObject {
  string TaskId; string RuleId;
  IReadOnlyList<AlgorithmName> AlgorithmNames /* JSON "algorithmName" */;
  string TenantId; IReadOnlyList<TilingConfig> TilingConfigs;
  string ImageId; JsonElement RoiFootprint;
  DateTimeOffset PhotoTime; string SensorType; string ImageUrl;
  int ImageWidth; int ImageHeight; double BestResolution;
  string SensorName; string? AreaOfInterest;
  string GridType; string GridUri /* JSON "gridURI" */;
}

public sealed class TbPublisherOutputMessageDto {
  FrameMetadataDto FrameMetadata; ModelMetadataDto ModelMetadata;
  string FocusedPxWkt; MissionMetadataDto MissionMetadata;
  string RequestId; string TaskId;
}

// v2 rule document (schema version 2)
public sealed class PipelineRuleDocument : IValidatableObject {
  const int CurrentSchemaVersion = 2;
  string Id /* "_id" */; int SchemaVersion; string RuleName; string? Description;
  bool IsActive = true; bool MatchAll; RuleMatchConditions? Match;
  List<JsonElement>? RunParams; string? CreatedBy; string? UpdatedBy;
  DateTimeOffset CreationTime; DateTimeOffset UpdateTime;
}
public sealed class RuleMatchConditions {   // every field optional = "no constraint"
  List<SensorConfig>? Sensors; ResolutionRange? Resolution;
  string? LocationWkt; JsonElement? Location; int? MaxPhotoAgeDays;
}
public sealed class ResolutionRange { double? Min; double? Max; }
public sealed class SensorConfig { string Name; List<RegistrationQuality> RegistrationQualities; List<string> GridTypes; }
public sealed class TilingConfig { int TileSizeWidth, TileSizeHeight, TileOverlapWidth, TileOverlapHeight; }

public enum AlgorithmName { FindAir, Rpn }
public enum RegistrationQuality { Accurate = 0, Sensor = 1 }
```

---

## 7. `libs/pipeline-catalog`

### Definitions & Purpose

`ImagingPipeline.PipelineCatalog` is the shared catalog of configured pipelines (rule index, contract id, transport) plus RabbitMQ connection definitions. It performs validation only — no network or Elasticsearch I/O. It depends on `pipeline-contracts` and `rabbitmq-configuration`, and is consumed by `apps/rules-api` and `apps/unified-gateway`.

### Execution Flow

A host calls `AddPipelineCatalogFileAsBase(contentRootPath)`, which resolves the catalog file via the `PipelineCatalogFile` setting (defaulting to `pipelinecatalog.json` next to the app binary) and inserts it as the lowest-priority configuration source, preserving `ExtraData`'s raw JSON shape. `services.AddPipelineCatalog(configuration)` binds `PipelineCatalogOptions` under section `PipelineCatalog` with `ErrorOnUnknownConfiguration = true`, runs validation (case-collision/`ExtraData` JSON checks) with `.ValidateOnStart()`, and registers `PipelineCatalog` as a singleton implementing `IPipelineCatalog`, `IRuleSourceResolver`, and `IRabbitMqConnectionResolver`. The `PipelineCatalog` constructor re-validates via `PipelineCatalogOptionsValidator` (cross-checking contract IDs against the registered contract registry), builds immutable `_all`/`_enabled`/`_byId` snapshots (normalizing RabbitMQ queue arguments once), and serves `GetAll()`, `GetEnabled()`, `GetRequired(id)`, `TryGet`, and — as `IRuleSourceResolver` — `Resolve(pipelineId) → RuleSource(IndexName, PipelineId)`.

### External Dependencies & Consumed Services

File I/O via `Microsoft.Extensions.Configuration.Json`; `pipeline-contracts` (`IPipelineContractRegistry`, to validate `ContractId` and call `ValidateExtraData`); `rabbitmq-configuration` for queue/exchange argument normalization.

### Configuration & Environment Variables

Config file: `libs/pipeline-catalog/configuration/pipelinecatalog.json`, copied into each consumer's output directory as `pipelinecatalog.json`. Section name: `PipelineCatalog`. Setting `PipelineCatalogFile` selects an alternate catalog path. Override example: `PipelineCatalog__Pipelines__FindAir__ExtraData` (must be a complete JSON string).

### Input & Output Message Schemas

```csharp
public sealed class PipelineCatalogOptions {
  const string SectionName = "PipelineCatalog";
  Dictionary<string, RabbitMqConnectionOptions> RabbitMqConnections;
  Dictionary<string, PipelineSettings> Pipelines;
}
public record PipelineSettings {
  bool Enabled; string ContractId; string RulesIndex;
  PipelineExtraData ExtraData; PipelineTransportOptions Transport;
}
public sealed record PipelineDefinition : PipelineSettings { string PipelineId; }
public enum PipelineTransportKind { RabbitMq = 1, Http = 2 }
public sealed record PipelineTransportOptions { PipelineTransportKind? Kind; RabbitMqTransportOptions? RabbitMq; HttpTransportOptions? Http; }
public sealed record RabbitMqTransportOptions { string ConnectionRef; string? VirtualHost; RabbitMqQueueOptions Output; }
public sealed record RabbitMqConnectionOptions { string Hostname; int Port = 5672; string Username; string Password; string VirtualHost = "/"; }
public sealed record RabbitMqQueueOptions { string QueueName; Dictionary<string,object?> Arguments; RabbitMqExchangeOptions ExchangeSettings; }
public sealed record RabbitMqExchangeOptions { bool ShouldBindToExchange; string ExchangeName; string ExchangeType = "direct"; string RoutingKey; Dictionary<string,object?> Arguments; Dictionary<string,object?> BindingArguments; }
public sealed record HttpTransportOptions { string Endpoint; string Method = "POST"; int TimeoutSeconds = 20; Dictionary<string,string> Headers; }

public interface IPipelineCatalog { GetAll(); GetEnabled(); GetRequired(string id); TryGet(string id, out PipelineDefinition pipeline); }
public interface IRuleSourceResolver { RuleSource Resolve(string pipelineId); }
public interface IRabbitMqConnectionResolver { RabbitMqConnectionOptions GetRequired(string connectionRef); }
public sealed record RuleSource(string IndexName, string PipelineId);
```

Catalog JSON shape (`configuration/pipelinecatalog.json`): top-level `PipelineCatalog.RabbitMqConnections` (keyed object) and `PipelineCatalog.Pipelines` (keyed **object**, not an array), each entry carrying `Enabled`, `ContractId`, `RulesIndex`, `ExtraData` (arbitrary JSON), and `Transport.Kind` (`"rabbitmq"` | `"http"`) with the matching `RabbitMq`/`Http` block.

---

## 8. `libs/pipeline-contracts`

### Definitions & Purpose

`ImagingPipeline.PipelineContracts` performs pure validation and payload-building per pipeline (FindAir, DeepWisdom) — no rule matching, no network calls. It depends on `common-dtos` and `geometry-utils`, and is consumed by `apps/rules-api`, `apps/unified-gateway`, and libs `pipeline-catalog` and `rule-engine`.

### Execution Flow

A caller builds a `PipelineDispatchContext` directly, or via `OverlayDispatchContextFactory.Create(taskId, ruleId, overlayJson)`, which projects a flat overlay JSON into the context — enforcing no duplicate fields, requiring `id`/`photoTime`, and always preserving the actual `roiFootprint` (never substituting the full-image footprint). `PipelineContractRegistry.GetRequired(contractId)` performs an ordinal lookup to resolve `IPipelineContract` for `"FindAir"`/`"DeepWisdom"`. The caller then runs `ValidateRunParams`, `ValidateExtraData`, `ValidateContext` (each returning field-level `ContractValidationError`s) before calling `BuildPayload(context, runParams, extraData)`, which re-validates and throws `ArgumentException` on failure, then emits an ordered-JSON `byte[]` body plus `ContentType`, `Attributes`, and optional `RabbitMqAttributes` (FindAir sets integer `findair-contract-version = 1`).

### External Dependencies & Consumed Services

`common-dtos` (`AlgorithmName`, `TilingConfig`, `TilingConfigValidator`), `geometry-utils` (overlay/geometry helpers), `Microsoft.Extensions.DependencyInjection.Abstractions` for DI registration extensions. No I/O.

### Configuration & Environment Variables

None. Contract IDs are hardcoded exact-case strings `"FindAir"` / `"DeepWisdom"`.

### Input & Output Message Schemas

```csharp
public interface IPipelineContract {
  string ContractId;
  IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams);
  IReadOnlyList<ContractValidationError> ValidateExtraData(JsonElement extraData);     // default: must be an object
  IReadOnlyList<ContractValidationError> ValidateRuleLocation(string? locationWkt);    // default: []
  IReadOnlyList<ContractValidationError> ValidateContext(PipelineDispatchContext context, JsonElement runParams = default);
  PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default);
}
public sealed record ContractValidationError(string Field, string Message);
public sealed record PipelinePayload(byte[] Body, string ContentType, IReadOnlyDictionary<string,string> Attributes, IReadOnlyDictionary<string,object?>? RabbitMqAttributes = null);
public sealed record PipelineDispatchContext(
  string TaskId, string RuleId, string ImageId, JsonElement RoiFootprint, DateTimeOffset PhotoTime,
  string SensorType, string ImageUrl, int ImageWidth, int ImageHeight, double BestResolution,
  string SensorName, string? AreaOfInterest, string GridType, string GridUri) {
  IReadOnlyList<ContractValidationError> InputFieldErrors;
  string? LegId; string? PrevOverlayId; string? NextOverlayId;
  string? RuleLocationWkt; DateTime? OverlayPhotoTime;
}
```

FindAir run-params JSON: `{ tenantId: string, algorithmNames: AlgorithmName[], tilingConfigs: TilingConfig[] }` — unknown or duplicate properties are rejected.

DeepWisdom run-params JSON (per README): `{ customer, profile_name?, hebrew_rule_name?, algorithm_name, priority: int, username?, run_every_other_image: bool, should_check_in_vip: bool }`; `ExtraData` optional `{ XUserName, Origin, QueueType, SaveDetections: bool }`.

---

## 9. `libs/rule-engine`

### Definitions & Purpose

`ImagingPipeline.RuleEngine` matches incoming image updates against pipeline rules; shared by `apps/unified-gateway` and `apps/rules-api`. It depends on `common-dtos`, `elasticsearch-client`, `geometry-utils`, `observability`, and `pipeline-contracts`.

### Execution Flow

1. **Input parsing** — `GatewayInputMessageParser.Parse(ReadOnlyMemory<byte> body)` deserializes into an internal DTO, requires non-empty `id`/`sensorName`/`registrationQuality` and a valid `RegistrationQuality` enum value, a positive finite `bestResolution`, valid GeoJSON `roiFootprint`, and a non-empty `gridType`; throws `InvalidInputMessageException` with stable error codes (e.g. `gateway.missing_image_id`) on failure. Pipeline-specific fields stay as `JsonElement`/best-effort optional values, deferred to contract validation. Produces a `GatewayInputMessage` record.
2. **Rule loading** — `ElasticsearchRuleRepository.GetActiveRulesAsync(indexName, ct)` opens an Elasticsearch point-in-time, pages 500-at-a-time filtering `isActive == true`, deserializes each hit into a `PipelineRuleDocument`, collecting per-document failures into non-fatal `RuleRejection`s; pagination/consistency violations throw a retryable `RuleLoadException`. Result: `RuleLoadResult(Rules, RejectedSources)`.
3. **Snapshot build** — `RuleSnapshotBuilder.Build(RuleLoadResult, IPipelineContract)` validates each document via `PipelineRuleValidator.Validate(rule, contract)` (document shape *and* each `runParams` entry against the pipeline's own contract), rejecting invalid rules individually with a reason and converting valid ones into `ActiveRule` records — precomputing sensor-quality bitmasks, resolution bounds, a nullable max-photo-age `TimeSpan`, and a parsed geometry. Returns a `RuleSnapshot` with an `AllRulesRejected` flag so callers keep a prior snapshot rather than route against nothing.
4. **Matching** — `RuleMatcher.Match(GatewayInputMessage, IReadOnlyList<ActiveRule>)` computes the input's registration-quality mask and photo age once, then for each rule checks in order: photo age → sensor (name + quality mask + grid type) → resolution range → geometry intersection (cheap `Intersects` test, then `Intersection`). The first failing condition is recorded as a `RuleMiss`; matches are recorded as `RuleMatchResult(ActiveRule, IntersectionGeometry)`. The matcher itself does not log — callers log rule-evaluation results.

### External Dependencies & Consumed Services

`elasticsearch-client` (`IElasticsearchPointInTimeClient`) for rule storage I/O; `common-dtos` for `PipelineRuleDocument`/`SensorConfig`/`RegistrationQuality`; `pipeline-contracts` (`IPipelineContract`) for run-params validation during snapshot build; `geometry-utils`/NetTopologySuite for GeoJSON parsing and geometry intersection; `observability`/`Microsoft.Extensions.Logging.Abstractions` for structured logging.

### Configuration & Environment Variables

None directly — the Elasticsearch index name is passed per-call, resolved upstream from `pipeline-catalog`'s `IRuleSourceResolver`, not configured within this library.

### Input & Output Message Schemas

```csharp
public sealed record GatewayInputMessage(
  string ImageId, string SensorName, string SensorType, RegistrationQuality RegistrationQuality,
  double BestResolution, string? AreaOfInterest, string ImageUrl, int ImageWidth, int ImageHeight,
  DateTimeOffset PhotoTime, Geometry Geometry, string GridType, string GridUri);

public interface IRuleRepository { Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken ct); }
public sealed record RuleLoadResult(IReadOnlyList<PipelineRuleDocument> Rules, IReadOnlyList<RuleRejection> RejectedSources);
public sealed record RuleRejection(string RuleId, string Reason, Exception? Exception = null);
public sealed class RuleLoadException(string message, Exception innerException) : Exception(message, innerException);

public sealed record ActiveRule(
  string Id, IReadOnlyDictionary<string, SensorMatchCriteria> Sensors,
  double MinResolution, double MaxResolution, TimeSpan? MaxPhotoAge,
  Geometry? Geometry, IReadOnlyList<JsonElement> RunParams);
public readonly record struct SensorMatchCriteria(int RegistrationQualityMask, IReadOnlySet<string>? AllowedGridTypes);

public sealed record RuleSnapshot(IReadOnlyList<ActiveRule> Rules, IReadOnlyList<RuleRejection> Rejections) { bool AllRulesRejected; }
public sealed record RuleMatchResult(ActiveRule Rule, Geometry IntersectionGeometry);
public sealed record RuleEvaluation(IReadOnlyList<RuleMatchResult> Matches, IReadOnlyList<RuleMiss> Misses);
public sealed record RuleMiss(string RuleId, RuleMissReason Reason);
public enum RuleMissReason { PhotoAge, Sensor, Resolution, Geometry }
```

---

## 10. `libs/elasticsearch-client`

### Definitions & Purpose

`ImagingPipeline.ElasticsearchClient` wraps NEST/`Elasticsearch.Net` with DI registration, DTO-based query building, and a generic document client. Registered via `AddElasticsearchClient(configuration)`, exposing `IElasticClient` (raw NEST) and `IElasticsearchDocumentClient`. Consumers: `libs/rule-engine`, `apps/rules-api`, `apps/gateway`, `apps/unified-gateway`.

### Execution Flow

A caller builds an `ElasticsearchSearchRequest`/`ElasticsearchSensorSearchRequest`/`ElasticsearchGeoShapeSearchRequest`, which the client translates into a NEST query (sensor filters build `sensors.{sensorName}.keyword` term queries; geo filters build `geo_shape` queries), then executes `SearchAsync`/`SearchDocumentsAsync`/`GetAsync`/`IndexAsync`/`DeleteAsync` against NEST, mapping hits to `TDocument`/`ElasticsearchDocument<TDocument>(Id, Source)`. `IndexAsync` supports `waitForRefresh`/`allowGeneratedId`; `DeleteAsync` returns a bool. Each operation emits an `ImagingPipeline.Elasticsearch` client span plus dependency metrics.

### External Dependencies & Consumed Services

An Elasticsearch cluster over HTTP(S). NuGet: `Elasticsearch.Net`, `NEST`, `Microsoft.Extensions.Options.ConfigurationExtensions`; project reference to `libs/observability`.

### Configuration & Environment Variables

Section `Elasticsearch`: `Uri` (default `http://localhost:9200`, must be an absolute http/https URL), `Index` (default `rules`), `TimeoutSeconds` (default 30, > 0), `Username`/`Password` (optional, must be set together). Env override pattern: `Elasticsearch__Uri`.

### Input & Output Message Schemas

```csharp
public interface IElasticsearchDocumentClient {
  Task<...> SearchAsync<T>(ElasticsearchSearchRequest request);
  Task<...> SearchBySensorAsync<T>(ElasticsearchSensorSearchRequest request);
  Task<...> SearchByGeoShapeAsync<T>(ElasticsearchGeoShapeSearchRequest request);
  Task<T?> GetAsync<T>(string indexName, string id);
  Task<ElasticsearchDocument<T>?> GetDocumentAsync<T>(string indexName, string id, CancellationToken ct);
  Task<...> SearchDocumentsAsync<T>(object requestOrDescriptor, CancellationToken ct);
  Task IndexAsync<T>(string indexName, string? id, T document, bool waitForRefresh = true, bool allowGeneratedId = false, CancellationToken ct = default);
  Task<bool> DeleteAsync<T>(string indexName, string id, bool waitForRefresh = true, CancellationToken ct = default);
}
public sealed record ElasticsearchTermFilter(string Field, object Value);
public sealed record ElasticsearchTermsFilter(string Field, IReadOnlyList<object> Values);
public sealed record ElasticsearchSensorFilter(string SensorRootField /* "sensors" */, string SensorName, IReadOnlyList<string> Values, string KeywordSuffix /* ".keyword" */);
public sealed record ElasticsearchGeoShapeFilter(string Field, JsonElement Shape, string Relation);
public sealed record ElasticsearchSearchRequest(string IndexName, int From, int Size = 100, IReadOnlyList<string>? SourceIncludes, IReadOnlyList<string>? ExcludedIds, IReadOnlyList<ElasticsearchTermFilter>? TermFilters, IReadOnlyList<ElasticsearchTermsFilter>? TermsFilters, IReadOnlyList<ElasticsearchSensorFilter>? SensorFilters, IReadOnlyList<ElasticsearchGeoShapeFilter>? GeoShapeFilters);
public sealed record ElasticsearchDocument<T>(string Id, T Source);
```

---

## 11. `libs/rabbitmq-client`

### Definitions & Purpose

`ImagingPipeline.RabbitMqClient` is a DI-friendly async RabbitMQ client built on `RabbitMQ.Client` 7.2.1, providing confirmed publish, single/batch consume, manual ack/nack, DLX/DLQ routing, durable topology declaration, and connection/topology recovery. Registered via `AddRabbitMqPublisher`, `AddRabbitMqConsumer`, `AddRabbitMqClient`, `AddRabbitMqDestinationPublisher`. Consumers: `apps/gateway`, `apps/unified-gateway`, `apps/tb-publisher`, `apps/tb-consumer`.

### Execution Flow

**Publish** — `IRabbitMqPublisher.PublishAsync(exchange, routingKey, envelope)` / `PublishToInputAsync` / `PublishToOutputAsync` lease a channel from `RabbitMqPublisherChannelPool`, set persistent delivery + publisher confirms, call `BasicPublishAsync`, and await confirmation.

**Consume** — `IRabbitMqConsumer.ConsumeAsync(handler)` declares topology (`RabbitMqTopology.DeclareAsync`) and calls `BasicConsumeAsync`. The handler returns a `RabbitMqMessageProcessingResult`: on success, the client publishes any output messages then acks; on non-retryable failure it nacks without requeue (routing to DLQ); on retryable failure it republishes with an incremented `retry-count` header to the appropriate retry queue/exchange.

**Connection management** — separate connections for publisher vs. consumer channels; the RabbitMQ client's built-in auto-recovery uses `ReconnectDelaySeconds`; at the application level, the consumer worker restarts `ConsumeAsync` on fault with exponential jittered backoff (capped at 30s).

### External Dependencies & Consumed Services

A RabbitMQ broker over AMQP. NuGet: `RabbitMQ.Client`, `Microsoft.Extensions.Hosting.Abstractions`; project references to `libs/rabbitmq-configuration` and `libs/observability`.

### Configuration & Environment Variables

Section `RabbitMq`: `Host` (`localhost`), `Port` (5672), `Username`/`Password` (`admin`/`admin`), `VirtualHost` (`/`), `InputCluster:{Host,Port,Username,Password,VirtualHost}` (optional second broker), `InputQueue`, `OutputQueue`, `DeadLetterQueue`, `RetryQueue`, `*Exchange`/`*ExchangeType` (default `direct`), `*RoutingKey`, `HeadersArguments`/`*QueueHeaders`/`*ExchangeHeaders`/`*BindingArguments` dictionaries, `PrefetchCount` (1), `ConsumerConcurrency` (1), `PublisherChannelPoolSize` (4), `OutputPublishConcurrency` (4), `RetryDelayMilliseconds` (10000), `MaxRetryAttempts` (3), `RetryCountHeader` (`retry-count`), `RetryQueues: [{RetryCount, Queue, DelayMilliseconds, BindingArguments}]`, `ReconnectDelaySeconds` (5), `ForwardedInputStage` (optional `PipelineStage`). Env override pattern: `RabbitMq__Host`.

### Input & Output Message Schemas

```csharp
public sealed record RabbitMqMessageEnvelope(string MessageId, byte[] Body, string ContentType = "application/json", IReadOnlyDictionary<string,object?>? Headers = null, string? CorrelationId = null) {
  static RabbitMqMessageEnvelope FromUtf8(string messageId, string body, ...);
  string BodyAsUtf8();
}
public sealed record RabbitMqMessageProcessingResult(bool IsSuccess, byte[]? OutputBody, string? Error, IReadOnlyList<RabbitMqMessageEnvelope>? OutputMessages, FailureAction? FailureAction) {
  static RabbitMqMessageProcessingResult Success();
  static RabbitMqMessageProcessingResult Success(byte[] outputBody);
  static RabbitMqMessageProcessingResult Success(IReadOnlyList<RabbitMqMessageEnvelope> outputMessages);
  static RabbitMqMessageProcessingResult Failure(string error);
  static RabbitMqMessageProcessingResult NonRetryableFailure(string error);
  static RabbitMqMessageProcessingResult RetryableFailure(string error);
}
public interface IRabbitMqMessageHandler { Task<RabbitMqMessageProcessingResult> HandleAsync(RabbitMqMessageEnvelope envelope, CancellationToken ct); }
public interface IRabbitMqBatchMessageHandler { Task<RabbitMqMessageProcessingResult> HandleBatchAsync(IReadOnlyList<RabbitMqMessageEnvelope> messages, CancellationToken ct); }

public sealed record RabbitMqDestination(RabbitMqDestinationConnection Connection, string QueueName, Dictionary<string,object?> QueueArguments, string? ExchangeName, string? ExchangeType, string? RoutingKey, bool BindQueueToExchange, Dictionary<string,object?>? ExchangeArguments, Dictionary<string,object?>? BindingArguments);
public sealed record RabbitMqDestinationConnection(string Name, string Host, int Port, string Username, string Password, string VirtualHost);
public interface IRabbitMqDestinationPublisher { Task PublishAsync(RabbitMqDestination destination, RabbitMqMessageEnvelope message, CancellationToken ct); }
```

---

## 12. `libs/rabbitmq-configuration`

### Definitions & Purpose

`ImagingPipeline.RabbitMqConfiguration` is a tiny, dependency-free argument normalizer shared by `rabbitmq-client` and `pipeline-catalog`. It does not connect to a broker or declare topology — that is `rabbitmq-client`'s `RabbitMqTopology` responsibility.

### Execution Flow

`RabbitMqArgumentNormalizer.Normalize(arguments, policy)` copies a `Dictionary<string,object?>`; for each string value, unless the key is a known string-only argument (`x-dead-letter-exchange`, `x-dead-letter-routing-key`, `x-queue-type`, `x-overflow`, `x-match`, `alternate-exchange`, `x-delayed-type`), it attempts invariant-culture conversion in order `int` → (policy-gated) `long` → `bool` → (policy-gated) `double`, otherwise keeping the original string. Topology declaration itself happens downstream in `rabbitmq-client`'s `RabbitMqTopology.DeclareAsync`, which uses these normalized arguments when declaring exchanges/queues/bindings.

### External Dependencies & Consumed Services

None — no NuGet packages, no broker access.

### Configuration & Environment Variables

No env vars; behavior is selected by a caller-supplied `RabbitMqArgumentConversionPolicy`: `Legacy` (used by `rabbitmq-client`: `Int32` → `Boolean`) or `ExtendedNumeric` (used by `pipeline-catalog`: `Int32` → `Int64` → `Boolean` → finite `Double`).

### Input & Output Message Schemas

```csharp
public static class RabbitMqArgumentNormalizer {
  static Dictionary<string, object?> Normalize(IEnumerable<KeyValuePair<string, object?>> arguments, RabbitMqArgumentConversionPolicy policy);
  static bool IsStringArgument(string key);
}
public enum RabbitMqArgumentConversionPolicy { Legacy, ExtendedNumeric }
```

---

## 13. `libs/projection-mapper-client`

### Definitions & Purpose

`ImagingPipeline.ProjectionMapperClient` is a typed `HttpClient` wrapper for the external Projection Mapper HTTP service (ground↔image coordinate transforms). Registered via `ProjectionMapperClientServiceCollectionExtensions`. Consumers: `apps/tb-publisher`, `apps/tb-consumer`.

### Execution Flow

A caller invokes `MapAsync(overlayId, ProjectionMapperRequestDto)` (ground→image, endpoint `G2IMultiPoints`), `ProcessBatchAsync(overlayId, coordinates)` (image→ground by id, endpoint `I2GById`), or `ProcessBatchByRegistrationAsync(overlayId, coordinates, gridType, gridUri)` (endpoint `I2GByRegistration`). The shared `ExecuteAsync<TRequest>` path starts an `ImagingPipeline.ProjectionMapper` activity span, resolves the configured endpoint, builds `POST {endpoint}?overlayId=...&useCache=...` (query omitted for the registration call) with a `sendingSystem` header and a JSON request body, sends via `HttpClient`, deserializes a `ProjectionMapperResponseDto`, and returns `payload.Coordinates`. Dependency telemetry (batch size, payload size, outcome/error classification) is recorded; config/HTTP/timeout/serialization failures throw `ProjectionMapperClientException`.

### External Dependencies & Consumed Services

The external Projection Mapper HTTP API (three `POST` endpoints). NuGet: `Microsoft.Extensions.Http`, `Microsoft.Extensions.Options.ConfigurationExtensions`; project reference to `libs/observability`.

### Configuration & Environment Variables

Section `ProjectionMapper`: `Host` (default `http://projection-mapper`, must be an absolute URL), `Endpoints: Dictionary<string,string>` must include `G2IMultiPoints`, `I2GById`, `I2GByRegistration`, `SendingSystem` (required, non-empty, sent as an HTTP header), `UseCache` (bool), `TimeoutSeconds` (default 10, ≥ 1).

### Input & Output Message Schemas

```csharp
public interface IProjectionMapperClient {
  Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(string overlayId, ProjectionMapperRequestDto request, CancellationToken ct);
  Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchAsync(string overlayId, IReadOnlyList<IReadOnlyList<double>> coordinates, CancellationToken ct);
  Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchByRegistrationAsync(string overlayId, IReadOnlyList<IReadOnlyList<double>> coordinates, string gridType, string gridUri, CancellationToken ct);
}
public sealed record ProjectionMapperRequestDto(IReadOnlyList<IReadOnlyList<double>> Coordinates);
public sealed record I2GByIdRequestDto(IReadOnlyList<IReadOnlyList<double>> Coordinates);
public sealed record I2GByRegistrationRequestDto(string OverlayId, bool ReturnAltitude, IReadOnlyList<IReadOnlyList<double>> PixelPoints, string GridType, string GridUri, bool UseCache);
public sealed record ProjectionMapperResponseDto(IReadOnlyList<IReadOnlyList<double>> Coordinates);
public sealed class ProjectionMapperClientException : Exception;
```

---

## 14. `libs/geometry-utils`

### Definitions & Purpose

`ImagingPipeline.GeometryUtils` is a static helper (`GeometryUtilities` class) over NetTopologySuite for reading/writing/validating 2D geometry (WKT and GeoJSON). It has no DI registration — pure static methods — and no observability dependency. Consumers: `libs/pipeline-contracts`, `libs/rule-engine`, `apps/gateway`, `apps/tb-publisher`.

### Execution Flow

`ReadWkt(string)` / `ReadGeoJson(JsonElement)` parse via `WKTReader`/`GeoJsonReader`, then `Force2D` strips any Z/M ordinates (keeping only X/Y), then `Validate` throws `GeometryValidationException` if the result `IsEmpty` or `!IsValid`. `ReadTrustedWkt`/`ReadTrustedGeoJson` skip validation for pre-validated configuration data. `CreatePolygonFromCoordinates(points)` requires ≥ 3 points, auto-closes an open ring, builds via `GeometryFactory`, then runs the same Force2D + Validate pipeline. `WriteWkt` uses a 2D-only `WKTWriter`; `WriteGeoJson` hand-writes GeoJSON via `Utf8JsonWriter`, switching on geometry type (Point/LineString/Polygon/MultiPoint/MultiLineString/MultiPolygon/GeometryCollection) and enforcing exterior-ring CCW / interior-ring CW orientation; unsupported geometry types throw `NotSupportedException`.

### External Dependencies & Consumed Services

No external service — pure library on NuGet `NetTopologySuite`, `NetTopologySuite.IO.GeoJSON`.

### Configuration & Environment Variables

None — stateless static API, no options class, no env vars.

### Input & Output Message Schemas

```csharp
public static class GeometryUtilities {
  static Geometry ReadWkt(string wkt);
  static Geometry ReadGeoJson(JsonElement geoJson);
  static Geometry ReadTrustedWkt(string wkt);
  static Geometry ReadTrustedGeoJson(JsonElement geoJson);
  static Geometry CreatePolygonFromCoordinates(IReadOnlyList<IReadOnlyList<double>> points);
  static string WriteWkt(Geometry geometry);
  static JsonElement WriteGeoJson(Geometry geometry);
  static void WriteGeoJson(Utf8JsonWriter writer, Geometry geometry);
  static Geometry Force2D(Geometry geometry);
  static Geometry Validate(Geometry geometry);   // throws GeometryValidationException
}
```
All operate on NetTopologySuite `Geometry` subtypes (`Point`, `LineString`, `Polygon`, `Multi*`, `GeometryCollection`).

---

## 15. `libs/observability`

### Definitions & Purpose

`ImagingPipeline.Observability` is the shared OpenTelemetry bootstrap and ECS logging contract used by every runnable application (`gateway`, `tb-publisher`, `tb-consumer`, `rules-api`, `unified-gateway`). It standardizes three independent signal paths: traces over OTLP → Collector → Elastic APM; logs as ECS-shaped JSON batched directly over HTTP to Logstash (never OTLP); metrics pulled by Prometheus via a `/metrics` endpoint. Per `docs/observability-contract.md`, missing or malformed configuration fails startup, while a Collector/Logstash outage must never stop business processing. The companion `docs/pipeline-telemetry-pattern.md` layers three reusable primitives — `PipelineStageScope`, `PipelineSpanScope`, `WorkloadDimensions` — on top of this contract so handler code doesn't hand-roll telemetry bookkeeping.

### Execution Flow

Apps call `builder.AddImagingPipelineObservability(serviceName, instrumentAspNetCore: …)`, which:
1. Registers `IMessageTraceContextPropagator` and forces W3C `ActivityIdFormat`.
2. Reads `RuntimeObservabilitySettings` and validates the OTLP endpoint/protocol when traces + OTLP are both enabled.
3. Configures the OTel `ResourceBuilder` with service name/namespace/version/instance plus deployment attributes.
4. Wires tracing: a parent-based ratio sampler when `OTEL_TRACES_SAMPLER` is absent, `AddSource` for all FindAir activity sources plus the RabbitMQ publisher/subscriber sources, HTTP client instrumentation, optional ASP.NET Core instrumentation (excluding the metrics path and health endpoints), and an optional OTLP exporter.
5. Wires metrics: runtime + HttpClient instrumentation, optional ASP.NET Core metrics, explicit-bucket histogram views, and an optional Prometheus exporter.
6. Configures ECS console/Logstash logging via `EcsLoggingRegistration.Configure(...)`.

Workers additionally call `ConfigureImagingPipelinePrometheusListener()` (binds Kestrel on the configured metrics port) and `MapImagingPipelinePrometheusScrapingEndpoint()` (maps `/metrics`, disabling HTTP metrics on that route itself); `rules-api` instead passes `instrumentAspNetCore: true` and reuses its API port. Per-request enrichment: RabbitMQ publisher/subscriber activity sources auto-create spans; `IMessageTraceContextPropagator` injects/extracts only W3C `traceparent`/`tracestate` (baggage is stripped); `TelemetryLogContext`/`AddPipelineContext` flatten business context onto both spans and ECS logs. **Bounded-cardinality allowlisting**: Prometheus labels are restricted to stage/direction/operation/outcome/dependency/bounded error category/rule/tenant/area/sensor/algorithm/tile-size — never raw IDs, URLs, payloads, or stack traces.

### External Dependencies & Consumed Services

- OTLP Collector (gRPC `:4317` or HTTP/protobuf `:4318`) via the OTLP exporter.
- Logstash HTTP input, POSTed from `EcsLogstashExporterService` as `Content-Type: application/json`.
- Prometheus, scraping the exposed `/metrics` endpoint via the Prometheus ASP.NET Core exporter.
- NuGet: `Microsoft.Extensions.Hosting`, `OpenTelemetry`, `OpenTelemetry.Api`, `OpenTelemetry.Exporter.OpenTelemetryProtocol`, `OpenTelemetry.Exporter.Prometheus.AspNetCore`, `OpenTelemetry.Extensions.Hosting`, `OpenTelemetry.Instrumentation.AspNetCore`, `OpenTelemetry.Instrumentation.Http`, `OpenTelemetry.Instrumentation.Runtime`.

### Configuration & Environment Variables

JSON tree under `Observability:`:

```
Observability:
  Enabled, ServiceNamespace, ServiceVersion, DeploymentEnvironment
  Traces: { Enabled, RecordExceptions, ExcludeHealthChecks, OtlpEnabled, DefaultSamplingRatio }
  Metrics: { Enabled, Prometheus: { Enabled, Path, Port } }
  Logs:
    Enabled, ConsoleEnabled
    Logstash: { Enabled, Endpoint, QueueCapacity, PriorityQueueCapacity, WarningQueueCapacity,
                BatchSize, FlushIntervalMilliseconds, RequestTimeoutSeconds, MaxRetryAttempts,
                RetryBaseDelayMilliseconds, ShutdownFlushTimeoutSeconds,
                MaxAttributeCount, MaxCollectionCount, MaxStringLength }
    DataStream: { Dataset, Namespace }
```

Standard OTEL env vars read directly: `OTEL_SDK_DISABLED`, `OTEL_SERVICE_NAME`, `OTEL_EXPORTER_OTLP_ENDPOINT`/`_PROTOCOL`, `OTEL_EXPORTER_OTLP_TRACES_ENDPOINT`/`_PROTOCOL` (signal-specific overrides the base endpoint), `OTEL_TRACES_SAMPLER`/`OTEL_TRACES_SAMPLER_ARG`. Custom env vars: `SERVICE_VERSION`, `DEPLOYMENT_ENVIRONMENT`, `DOTNET_ENVIRONMENT`/`ASPNETCORE_ENVIRONMENT`, `POD_NAME`, `POD_UID`, `POD_NAMESPACE`, `DEPLOYMENT_NAME`, `NODE_NAME`, `CONTAINER_NAME`, `CLUSTER_NAME`. `OTEL_METRICS_EXPORTER=none` / `OTEL_LOGS_EXPORTER=none` are recommended (to avoid duplicate exporters) but not enforced by this library.

### Input & Output Message Schemas

ECS log document (built by `EcsLogDocumentSerializer.BuildDocument`):

```json
{
  "@timestamp": "ISO-8601",
  "message": "string",
  "ecs.version": "8.11.0",
  "data_stream": { "type": "logs", "dataset": "string", "namespace": "string" },
  "service": { "name": "...", "namespace": "...", "version": "...", "environment": "...", "node": { "name": "..." } },
  "kubernetes": { "namespace": "...", "pod": { "name": "...", "uid": "..." }, "deployment": { "name": "..." }, "node": { "name": "..." }, "container": { "name": "..." } },
  "orchestrator": { "cluster": { "name": "...", "type": "..." } },
  "event": { "kind": "event", "dataset": "...", "code": "...", "action": "..." },
  "log": { "level": "...", "logger": "..." },
  "trace.id": "...", "span.id": "...",
  "error": { "type": "...", "message": "...", "stack_trace": "..." },
  "messaging.message.id": "...", "messaging.destination.name": "...",
  "findair.task.id": "...", "findair.request.id": "...", "findair.image.id": "...",
  "findair.rule.id": "...", "findair.tenant.id": "...", "findair.algorithm.names": ["..."],
  "findair.tile.id": "...", "findair.tile.index": 0, "findair.tile.count": 0,
  "http.request.method": "...", "url.path": "...", "http.route": "...", "url.full": "...",
  "http.response.status_code": 0,
  "labels.<snake_case_attribute>": "..."
}
```

Batches are serialized as a JSON array and POSTed to Logstash.

Trace propagation: `W3CMessageTraceContextPropagator` injects/extracts only `traceparent`/`tracestate`; `baggage` is explicitly stripped on inject.

Metric naming: OTel dot-separated names (e.g. `findair.logs.dropped`, `findair.stage.duration`, `messaging.client.sent.messages`) become Prometheus names via dots→underscores and the standard `_total`/`_bucket`/`_sum`/`_count` suffixing (e.g. `findair_logs_dropped_total`, `findair_stage_duration_bucket`). Log-export health instruments: `findair.logs.queue.size` (Gauge), `findair.logs.dropped` (Counter, label `findair.logs.drop.reason` ∈ `queue_full|priority_queue_full|formatting|serialization|transport|shutdown`), `findair.logs.export.requests`/`findair.logs.export.duration` (labels `findair.outcome`, `http.response.status_code_class`).

---

## 16. `deploy/observability`

### Definitions & Purpose

Copyable configuration fragments (not full deployments) for the shared Elastic observability stack: a Collector OTLP trace-receiver fragment, Logstash pipeline fragments for two existing Logstash deployments, a Prometheus scrape-job fragment, OpenShift Deployment env/annotation fragments, an Elasticsearch/Kibana data-stream template, and a Grafana dashboard. This bundle explicitly does **not** deploy Logstash/Collector/Prometheus itself, nor apply Elasticsearch changes automatically — operators copy the relevant fragment into their own existing infrastructure.

### Execution Flow

- **Traces**: app → OTLP Collector (gRPC `:4317` / HTTP `:4318`) → `memory_limiter` → `batch` (1s timeout, 1024 batch size) → trace-pipeline exporter placeholder (`YOUR_EXISTING_FLEET_APM_EXPORTER`) → Fleet-managed Elastic APM → Elasticsearch.
- **Logs**: app → Logstash HTTP input (`findair-production.conf`, port `8081`; dev port `8082`) → a `mutate` filter force-sets `event.dataset`/`data_stream.*` fields regardless of what the app sent, tagging JSON-parse failures → an `elasticsearch` output with `data_stream=true`, fixed `data_stream_dataset=findair`, `data_stream_namespace=production`, TLS + basic auth sourced from env vars. Logstash returns HTTP `202` on accept; indexing into `logs-findair-production` happens asynchronously.
- **Metrics**: app `/metrics` ← Prometheus `kubernetes_sd_configs` (role: `pod`) scoped to project `YOUR_OPENSHIFT_PROJECT`, keeping only pods annotated `prometheus.io/scrape=true` with label `app.kubernetes.io/part-of=findair` and phase `Running`, deriving `__metrics_path__`/`__address__` from pod annotations, and relabeling only the bounded resource labels.

### External Dependencies & Consumed Services

Logstash 8.5.3 (ECS v8 output plugin), Elasticsearch 8.15.0 (data-stream lifecycle management), Prometheus 2.36.1 (Kubernetes service discovery + pull scrape) — version scope stated explicitly in the component README. Elastic APM/Fleet and the Collector distribution/version are externally owned and intentionally absent from this repo. The Grafana dashboard declares two datasource inputs: Prometheus ("scrapes the FindAir services") and Elasticsearch (configured with `logs-findair-*` and `@timestamp`), with panel queries such as `sum(up{job=~"$job",service_name=~"$metric_service"})`.

### Configuration & Environment Variables

- **Collector fragment**: `memory_limiter { check_interval: 1s, limit_percentage: 75, spike_limit_percentage: 15 }`, `batch { timeout: 1s, send_batch_size: 1024 }`.
- **Logstash**: pipeline ports `8081`/`8082`; env vars `FINDAIR_LOGS_ELASTICSEARCH_HOST`/`USER`/`PASSWORD`/`CA` sourced from a `logstash-elasticsearch` Secret plus a CA volume mount; `pipelines.production.yml.example` sets `pipeline.id: findair-production`, `pipeline.ecs_compatibility: v8`, `pipeline.workers: 2`, `pipeline.batch.size: 125`, persisted queue `queue.max_bytes: 1gb`, `dead_letter_queue.enable: true` — the pre-existing `existing-system` pipeline is left untouched.
- **Prometheus**: `scrape_interval: 15s`, `scrape_timeout: 10s`, namespace placeholder `YOUR_OPENSHIFT_PROJECT`.
- **OpenShift app fragment**: labels `app.kubernetes.io/{name,version,part-of}`, `environment`; annotations `prometheus.io/scrape=true`, `prometheus.io/path=/metrics`, `prometheus.io/port=9464`; and the full `Observability__*`/`OTEL_*` env var set plus Downward-API fields (`POD_NAME`, `POD_UID`, `POD_NAMESPACE`, `NODE_NAME`) mirroring §15's configuration keys.

### Input & Output Message Schemas

- **Data-stream naming**: `logs-<dataset>-<namespace>` → `logs-findair-production`, `logs-findair-integration`, `logs-findair-development`, composed of `logs@mappings`, `logs@settings`, `ecs@mappings`, plus a custom `findair_logs_lifecycle` ILM policy with 7-day retention.
- **Test log payload** (validation example):
  ```json
  {
    "@timestamp": "...",
    "ecs": { "version": "8.0.0" },
    "message": "...",
    "log": { "level": "information", "logger": "..." },
    "service": { "name": "findair-gateway", "namespace": "findair", "version": "test", "environment": "production" },
    "event": { "dataset": "findair" }
  }
  ```
  Sent as a root JSON array; accepted with HTTP `202`.
- **OTLP payload**: standard OTLP/gRPC or OTLP/HTTP-protobuf trace spans, batched by the Collector before forwarding to Fleet APM (no `/v1/traces` suffix should be appended to the base endpoint for HTTP/protobuf).
- **Prometheus exposition**: standard Prometheus text format scraped from `http://<pod-ip>:9464/metrics` (workers) or `:8080/metrics` (Rules API), validated with `promtool check config` and the `up{job="findair"}` query.
