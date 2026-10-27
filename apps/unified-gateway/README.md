# Unified gateway

Consumes image updates, matches independently refreshed pipeline rules, prepares one unit per matched run, and dispatches through RabbitMQ or HTTP. The supported pipeline contracts are **FindAir**, **DeepWisdom** and **SarAtc**.

## Catalog

The gateway defaults to the shared [`pipelinecatalog.json`](../../libs/pipeline-catalog/configuration/pipelinecatalog.json), copied beside the application. Pipeline keys and contract IDs are `FindAir`, `DeepWisdom` and `SarAtc`, with exact casing. `PipelineCatalogFile` selects a complete replacement catalog; relative paths resolve against the host content root. Environment variables and command-line arguments override catalog values using the same pipeline spelling.

The gateway and Rules API use the same default catalog and contract names. Custom catalogs must use `FindAir`, `DeepWisdom` or `SarAtc` as contract IDs. Stored rule indexes and existing downstream queues retain their configured names.

Each enabled pipeline has its own `RulesIndex`, `ExtraData`, and exactly one transport. Rule indexes are explicit deployment settings; no index is created or migrated. Startup loads every enabled pipeline before consuming. Refresh failures retain that pipeline's previous rules. A nonempty load where every rule is rejected also retains the last usable snapshot.

## ExtraData

For `FindAir` and `DeepWisdom`, `ExtraData` is an arbitrary JSON object with no required property names or field schema. Every configured key and value is preserved under `extraData` in the outgoing JSON body, whether transported in a RabbitMQ message or an HTTP request. Objects, arrays, numbers, strings, booleans, nulls, Unicode, case-distinct names and literal colons retain their JSON meaning. Empty or omitted ExtraData adds no `extraData` property.

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

An update is considered only when `x-updated-fields` contains `gridType`. Common validation checks JSON structure, image identity, photo time, sensor name, registration quality, resolution, grid type and geometry needed for rule matching. Every accepted overlay uses the currently loaded active rules. Each rule applies its own `maxPhotoAgeDays` when configured; rules without it impose no age limit.

FindAir validates image URL, dimensions, sensor type, grid URI and optional area metadata when preparing its payload. DeepWisdom validates relationship fields only for runs using `runEveryOtherImage`. Missing or malformed fields belonging to one contract do not prevent another matched contract from dispatching. Failures remain associated with their pipeline; the source can still be dead-lettered after valid siblings are sent.

Debug event `6022` explains filtered work using `unified_gateway.filter.reason`: `grid_type_not_updated`, `no_enabled_pipelines`, `no_matching_rules`, or `duplicate_run`. The log includes the source message ID and pipeline/rule scopes when available. To see these logs, set `Logging__LogLevel__ImagingPipeline.UnifiedGateway.Source.SourceMessageHandler=Debug`.

## Delivery and retry

Dispatch IDs are 64 hexadecimal characters from a SHA-256 hash of the structured image ID, pipeline ID, rule ID and canonical run parameters. Object property order does not change the ID; array order remains meaningful. Hashing the complete identity prevents delimiter collisions and keeps AMQP `MessageId` within its byte limit. FindAir's task ID uses the same value.

RabbitMQ sets `MessageId` to the dispatch ID and `CorrelationId` to the source message ID, publishes persistently, and waits for publisher confirmation. It forwards contract attributes and the pipeline start timestamp; trace context comes from the current span.

HTTP sends the prepared JSON bytes with the configured method, endpoint and timeout. Catalog headers are applied to the appropriate request or content header collection, followed by payload attributes and `Idempotency-Key`. Unsupported headers fail explicitly. Redirects are not followed. Responses classify as delivered for 2xx, retryable for 408/429/5xx, and rejected otherwise. Connection failures and timeouts are retryable.

A retryable unit retries the whole original source message, which is matched again. Successful siblings can be delivered again with the same dispatch ID; downstream receivers must deduplicate. Otherwise, any rejected/invalid unit dead-letters the source. Delivery listeners do not change successful delivery outcomes. Retry topology remains unchanged under the planned cutover that removes the legacy gateway and its broker topology.

## Observability

The service exposes `/health` and `/metrics` on the configured Prometheus listener (default port 9464). Health identifies host capabilities; it is not a broker or Elasticsearch connectivity probe. No pipeline metadata routes are exposed here.

The shared console JSON formatter preserves arrays, typed counts and UTF-8 fields instead of printing CLR type names such as `System.String[]`. ECS/Logstash serialization preserves the same field values. The Logstash dataset is `unified_gateway`, a valid data-stream component.

Preparation, rule loading and dispatch have separate spans and metrics. Dispatch logs include pipeline identity, transport, dispatch/source IDs, outcome and HTTP status when present. Metric labels use bounded outcomes/error categories and configured pipeline IDs, not image IDs or arbitrary ExtraData. Bodies, credentials and ExtraData values are not logged.

## Build and validation

```powershell
dotnet test tests/unified-gateway-tests/ImagingPipeline.UnifiedGateway.Tests.csproj
dotnet test tests/pipeline-contracts-tests/ImagingPipeline.PipelineContracts.Tests.csproj
dotnet test tests/pipeline-catalog-tests/ImagingPipeline.PipelineCatalog.Tests.csproj
dotnet test tests/rule-engine-tests/ImagingPipeline.RuleEngine.Tests.csproj
npx nx build unified-gateway
npx nx run unified-gateway:docker-build
```

Unit tests use fake transports/repositories and do not establish live broker or downstream endpoint compatibility. Deployments must supply their endpoints, credentials, rule indexes and catalog.
