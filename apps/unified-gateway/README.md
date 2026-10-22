# Unified gateway

Consumes image updates, matches independently refreshed pipeline rules, prepares one unit per matched run, and dispatches through RabbitMQ or HTTP. The supported pipeline contracts are **FindAir**, **DeepWisdom** and **SarAtc**.

## Configuration

`appsettings.json` holds the host settings; pipelines live in the catalog (next section). Override any key with environment variables using `Section__Key`.

| Section | Keys and defaults | Meaning |
|---|---|---|
| `RabbitMq` | `Host` `localhost`, `Port` `5672`, `VirtualHost` `/`, `Username`/`Password` | Source broker for the input queue. Output destinations use catalog connections instead. |
| `RabbitMq` | `InputQueue` `unified-gateway.input.q`, `DeadLetterQueue` `unified-gateway.dlq.q` | Source queue and its dead-letter queue. |
| `RabbitMq` | `RetryExchange` `pipeline.retry.x` (`headers`), `RetryRoutingKey` `unified-gateway.retry`, `MaxRetryAttempts` `3`, `RetryQueues` (three queues, `DelayMilliseconds` `10000`, bound on `retry-count`) | Delayed retry of the whole source message; see [Delivery and retry](#delivery-and-retry). |
| `RabbitMq` | `PrefetchCount` `2`, `ConsumerConcurrency` `4`, `PublisherChannelPoolSize` `8`, `ReconnectDelaySeconds` `5` | Consumer throughput; the pool size also applies to each output broker connection. |
| `Elasticsearch` | `Uri` `http://localhost:9200`, `Username`/`Password` empty, `TimeoutSeconds` `30` | Rule store. Set both credentials or neither. Each pipeline's index comes from its catalog `RulesIndex`. |
| `RuleRefresh` | `IntervalSeconds` `60`, `JitterSeconds` `5` | Per-pipeline background reload interval plus random jitter. `IntervalSeconds` must be > 0 and `JitterSeconds` >= 0, or startup fails. |
| `HistoricalInput` | `Enabled` `false` | Accept historical auto-run envelopes; see [Historical auto-run input](#historical-auto-run-input). |
| `Observability` | Prometheus `Port` `9464`, log dataset `unified_gateway`, OTLP and Logstash off | Shared observability bootstrap. |

Never commit real hosts or credentials; supply them through deployment secrets.

## Catalog

The gateway defaults to the shared [`pipelinecatalog.json`](../../libs/pipeline-catalog/configuration/pipelinecatalog.json), copied beside the application. Pipeline keys and contract IDs are `FindAir`, `DeepWisdom` and `SarAtc`, with exact casing. `PipelineCatalogFile` selects a complete replacement catalog; relative paths resolve against the host content root. Environment variables and command-line arguments override catalog values using the same pipeline spelling.

The gateway and Rules API use the same default catalog and contract names. Custom catalogs must use `FindAir`, `DeepWisdom` or `SarAtc` as contract IDs. Stored rule indexes and existing downstream queues retain their configured names.

Each enabled pipeline has its own `RulesIndex`, `ExtraData`, and exactly one transport. Rule indexes are explicit deployment settings; no index is created or migrated. Startup loads every enabled pipeline before consuming; if any pipeline fails to load, the host stops instead of consuming. Refresh failures retain that pipeline's previous rules. A nonempty load where every rule is rejected also retains the last usable snapshot.

## ExtraData

For all three contracts, `ExtraData` is an arbitrary JSON object with no required property names or field schema. Every configured key and value is preserved under `extraData` in the outgoing JSON body, whether transported in a RabbitMQ message or an HTTP request. Objects, arrays, numbers, strings, booleans, nulls, Unicode, case-distinct names and literal colons retain their JSON meaning. Empty or omitted ExtraData adds no `extraData` property.

```json
"ExtraData": {
  "customerFlag": true,
  "priorityHint": 7,
  "options": { "mode": "custom" },
  "tags": ["example", null]
}
```

DeepWisdom additionally maps `Origin`, `QueueType`, `SaveDetections` and `XUserName`, when supplied, to its existing top-level output fields. These names are optional; their original entries also remain in `extraData`. Rule `username` takes precedence over the `XUserName` fallback unless it is null. Arbitrary additions do not overwrite rule-derived fields such as `priority` or `tasksData`.

Native JSON objects belong in the selected catalog file. Appsettings/environment/CLI overrides supply a complete JSON string, for example `PipelineCatalog__Pipelines__FindAir__ExtraData={"custom":true}`. The object is replaced as a whole; nested configuration-key overrides are rejected to avoid losing JSON types. Catalog changes require restarting the host.

## RabbitMQ virtual hosts

The named connection supplies broker credentials and a default virtual host. Each pipeline can override that virtual host inside its RabbitMQ transport:

```json
"RabbitMq": {
  "ConnectionRef": "findair-output",
  "VirtualHost": "/findair",
  "Output": {
    "QueueName": "int.algo.tb_publisher",
    "ExchangeSettings": { "ShouldBindToExchange": false }
  }
}
```

`Transport.RabbitMq.VirtualHost` takes precedence over `RabbitMqConnections.<ConnectionRef>.VirtualHost`. Omit it or use null to inherit the connection value. Explicit empty or whitespace-only values fail validation. The override does not change the shared connection or another pipeline's destination. Connections to different virtual hosts are isolated.

The source consumer uses the application's `RabbitMq` section. Output transports use only the selected catalog destination. The shared RabbitMQ client allows consumers to omit `OutputQueue`, so the gateway needs no special registration option.

## Input validation and filtering

An update is considered only when `x-updated-fields` contains `gridType`. Common validation checks JSON structure, image identity, photo time, sensor name, registration quality, resolution, grid type and geometry needed for rule matching.

FindAir validates image URL, dimensions, sensor type, grid URI and optional area metadata when preparing its payload. DeepWisdom validates relationship fields only for runs using `runEveryOtherImage`. SarAtc requires only the image ID. Missing or malformed fields belonging to one contract do not prevent another matched contract from dispatching. Failures remain associated with their pipeline; the source can still be dead-lettered after valid siblings are sent.

Debug event `6022` explains filtered work using `unified_gateway.filter.reason`: `grid_type_not_updated`, `no_enabled_pipelines`, `no_matching_rules`, or `duplicate_run`. The log includes the source message ID and pipeline/rule scopes when available. To see these logs, set `Logging__LogLevel__ImagingPipeline.UnifiedGateway.Source.SourceMessageHandler=Debug`.

## Historical auto-run input

The Rules API can publish a versioned historical envelope to the existing gateway input queue. This path is disabled by default. Deploy the gateway with `HistoricalInput__Enabled=true` **before enabling auto-run publication in the Rules API**. Every consumer of that queue must support the historical contract; an older gateway may acknowledge the envelope through its live update filter without processing it. The producer must use the same queue and declaration arguments, including its configured dead-letter settings.

```json
{
  "kind": "historical-auto-run",
  "version": 1,
  "runId": "auto-run-unique-id",
  "overlay": {
    "id": "overlay-id",
    "sensorName": "camera",
    "sensorType": "EO",
    "registrationQuality": "Accurate",
    "bestResolution": 0.7,
    "photoTime": "2020-01-02T10:00:00Z",
    "imageUrl": "https://images.example/overlay-id",
    "width": 100,
    "height": 100,
    "gridType": "grid",
    "gridURI": "https://images.example/overlay-id/grid",
    "roiFootprint": {
      "type": "Polygon",
      "coordinates": [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]]
    }
  },
  "targets": [
    {
      "pipelineId": "FindAir",
      "rule": {
        "_id": "saved-rule-id",
        "schemaVersion": 2,
        "ruleName": "Historical example",
        "isActive": false,
        "match": { "maxPhotoAgeDays": 1 },
        "runParams": [
          {
            "tenantId": "example",
            "algorithmNames": ["FindAir"],
            "tilingConfigs": [
              { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 0, "tileOverlapHeight": 0 }
            ]
          }
        ]
      }
    }
  ]
}
```

The envelope carries the exact saved rule snapshots frozen by preview. Historical messages require no `x-updated-fields` header. Each target must identify an enabled pipeline in the gateway catalog and a valid saved rule ID. Matching uses only the supplied snapshots; live rule refreshes and other matching pipelines cannot add work. Inactive saved rules are accepted. The original rule is validated before removing only `maxPhotoAgeDays`; sensor, quality, grid, resolution, location and `modeSAR` conditions still apply. Geometry clipping and pipeline-specific input requirements are the same as live dispatch.

All targets, matches and pipeline payloads are validated before any target is delivered. Missing fields, unsupported versions or kinds, duplicate JSON keys, duplicate pipeline/rule targets, disabled pipelines, nonmatching targets and invalid contract fields dead-letter the envelope. They never fall back to live matching. Top-level `kind` is reserved as the envelope discriminator.

Historical dispatch IDs include `runId` plus the existing image/pipeline/rule/canonical-parameters identity. Retrying the same envelope preserves IDs; deliberately creating a new run produces new IDs. A retryable downstream failure retries the entire envelope, including siblings that already succeeded. Downstream receivers still need deduplication. Rules API run completion means publication to the gateway input queue was confirmed; it does not mean downstream processing finished. Gateway traces and logs include `auto_run.run_id` and `unified_gateway.input_kind` for correlation.

## Delivery and retry

Dispatch IDs are 64 hexadecimal characters from a SHA-256 hash of the structured image ID, pipeline ID, rule ID and canonical run parameters. Object property order does not change the ID; array order remains meaningful. Hashing the complete identity prevents delimiter collisions and keeps AMQP `MessageId` within its byte limit. FindAir's task ID uses the same value.

RabbitMQ sets `MessageId` to the dispatch ID and `CorrelationId` to the source message ID, publishes persistently with `mandatory` set, and waits for publisher confirmation. Before the first publish on each channel, the gateway declares the destination queue, exchange and binding with the catalog arguments. Arguments that differ from an existing queue make the declaration fail, and the unit is treated as retryable. It forwards contract attributes and the pipeline start timestamp; trace context comes from the current span.

HTTP sends the prepared JSON bytes with the configured method, endpoint and timeout. Catalog headers are applied to the appropriate request or content header collection, followed by payload attributes and `Idempotency-Key`. Unsupported headers fail explicitly. Redirects are not followed. Responses classify as delivered for 2xx, retryable for 408/429/5xx, and rejected otherwise. Connection failures and timeouts are retryable.

A retryable unit retries the whole original source message, which is matched again. Successful siblings can be delivered again with the same dispatch ID; downstream receivers must deduplicate. Otherwise, any rejected/invalid unit dead-letters the source. Delivery listeners do not change successful delivery outcomes.

A retryable source message is published to `RetryExchange` with an incremented `retry-count` header. The matching retry queue holds it for its `DelayMilliseconds` and dead-letters it back to the input queue. After `MaxRetryAttempts` retries, or on any non-retryable result, the message goes to `DeadLetterQueue`.

## Observability

The service exposes `/health` and `/metrics` on the configured Prometheus listener (default port 9464). Health always returns `{"status":"Healthy","capabilities":["catalog","contracts","rules","dispatch"],"dispatchActive":true}`; it is not a broker or Elasticsearch connectivity probe. No pipeline metadata routes are exposed here.

The shared console JSON formatter preserves arrays, typed counts and UTF-8 fields instead of printing CLR type names such as `System.String[]`. ECS/Logstash serialization preserves the same field values. The Logstash dataset is `unified_gateway`, a valid data-stream component.

Preparation, rule loading and dispatch have separate spans and metrics: `unified_gateway.contract.preparations`/`.preparation.duration`, `unified_gateway.rules.loads`/`.rules.load.duration` and `unified_gateway.dispatch.units`/`.dispatch.duration`. Labels and the shared rules are in the [observability contract](../../docs/observability-contract.md). Log event IDs are 6001-6003 (dispatch), 6010-6013 (rule loading) and 6020-6022 (source messages). Dispatch logs include pipeline identity, transport, dispatch/source IDs, outcome and HTTP status when present. Metric labels use bounded outcomes/error categories and configured pipeline IDs, not image IDs or arbitrary ExtraData. Bodies, credentials and ExtraData values are not logged.

## Build and validation

```bash
dotnet test tests/unified-gateway-tests/ImagingPipeline.UnifiedGateway.Tests.csproj
dotnet test tests/pipeline-contracts-tests/ImagingPipeline.PipelineContracts.Tests.csproj
dotnet test tests/pipeline-catalog-tests/ImagingPipeline.PipelineCatalog.Tests.csproj
dotnet test tests/rule-engine-tests/ImagingPipeline.RuleEngine.Tests.csproj
npx nx build unified-gateway
npx nx run unified-gateway:docker-build
```

Unit tests use fake transports/repositories and do not establish live broker or downstream endpoint compatibility. Deployments must supply their endpoints, credentials, rule indexes and catalog.

## Dependencies

- Libraries: `libs/pipeline-catalog`, `libs/pipeline-contracts`, `libs/rule-engine` (parsing, matching, rule snapshots), `libs/elasticsearch-client`, `libs/rabbitmq-client` (consumer, retry routing, destination publisher), `libs/observability`.
- External systems: the source RabbitMQ broker, Elasticsearch (one rules index per enabled pipeline), and each pipeline's output broker or HTTP endpoint.

## Gotchas

- The host fails startup if any enabled pipeline's rules cannot be loaded, so an unreachable Elasticsearch or a missing index blocks the whole gateway, not just one pipeline.
- A retry resends the whole source message and rematches it against the current rules, so siblings are delivered again with the same dispatch ID. Downstream receivers must deduplicate on `MessageId` or `Idempotency-Key`.
- Output queues are declared by the gateway. Catalog `Arguments` must match any existing queue exactly.
- `/health` stays `Healthy` while brokers, Elasticsearch or endpoints are down; use dispatch and rule-load metrics for readiness.
- Enable `HistoricalInput` on every gateway consuming the input queue before the Rules API starts publishing envelopes.

---
Last verified: 2026-10-22 against commit d128873
