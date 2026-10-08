# Unified gateway: Catalog & Contracts Foundation

This Nx application implements the **Catalog & Contracts Foundation** ticket. It hosts a validated pipeline catalog and prepares contract payloads for an already matched pipeline. The existing gateway and Rules API retain their current behavior.

**Transport & Dispatch is in progress.** The dispatch core and the RabbitMQ and HTTP transports exist (see [Dispatch](#dispatch)), but nothing calls them yet: there is no source consumer, so starting the application does not open broker connections or contact downstream endpoints.

## Retained features

| Feature | Current behavior |
|---|---|
| Nx application | Separate `apps/unified-gateway` application with build, run, publish and Docker targets. |
| Shared catalog | `libs/pipeline-catalog` provides pipeline lookup, enabled entries, named RabbitMQ connection resolution and rule-source resolution. Runtime values are copied into a stable catalog; callers cannot change it by mutating returned collections. |
| Pipeline identity | Each key under `PipelineCatalog.Pipelines` is the pipeline ID. Runtime descriptors retain `PipelineId`; configuration entries have no nested ID. |
| Contract selection | `ContractId` selects a registered implementation that validates run parameters and builds payload bytes and attributes. It is independent of transport choice. |
| Rule-source descriptor | Each pipeline has an explicit `RulesIndex`. `IRuleSourceResolver` returns its index and pipeline ID without querying Elasticsearch. |
| Output descriptor | Exactly one `http` or `rabbitmq` descriptor per pipeline, with validated settings. |
| Contracts | `asd` preserves the existing tenant/algorithm/tiling payload. `algo` builds the Algo Manager mission body from overlay data, one rule's parameters and that pipeline's settings. |
| Extra data | `ExtraData` is the per-pipeline contract configuration, preserving JSON types. Algo consumes named settings into existing body fields; ASD retains nested `extraData` passthrough. There is no separate `ContractSettings` section. |
| Work preparation | `PipelineWorkPreparer` receives a pipeline ID, event context and supplied run parameters, then returns `Prepared`, `Disabled` or `Invalid`. It never matches rules or performs delivery. |
| HTTP endpoints | `/health` and `/metrics` only. Pipeline metadata remains available through the catalog in-process; no metadata routes are added to the Rules API. |
| Observability | Shared host logs, traces, runtime/ASP.NET metrics, Prometheus scraping and preparation-specific logs, spans and metrics. |
| Tests | Catalog/contract validation, named configuration overrides, ASD compatibility, Algo mapping and nullable fields, overlay projection, preparation outcomes and absence of metadata routes. |

There is no rule cache, rule loading, rule matching, snapshot refresh, spatial index, rule migration or Rules API change in this ticket.

## Run and test

From the repository root:

```powershell
npx nx restore unified-gateway
npx nx build unified-gateway
npx nx run unified-gateway:run
npx nx run-many -t test --projects=unified-gateway-tests,pipeline-catalog-tests,pipeline-contracts-tests
npx nx publish unified-gateway
npx nx run unified-gateway:docker-build
```

The checked-in Prometheus listener uses port **9464**, serving `/health` and `/metrics`. `/health` identifies the foundation capabilities and inactive dispatch workflow. It does not check broker or Elasticsearch connectivity. Pipeline metadata routes return 404. Change `Observability__Metrics__Prometheus__Port` when another local service uses that port.

## Deployment configuration

The shared catalog lives in [`libs/pipeline-catalog/configuration/pipelinecatalog.json`](../../libs/pipeline-catalog/configuration/pipelinecatalog.json). `appsettings.json` contains `Observability` and standard host/logging settings. There is no active `Gateway.Input`, `Gateway.Retry` or `Gateway.RabbitMq` runtime configuration.

The catalog library copies `pipelinecatalog.json` to build and publish output, including consuming applications. The gateway loads it from its application directory, independently of the working directory. Future services can reference the library and use `AddPipelineCatalogJsonFile(path)`; the Rules API is not changed here.

Startup uses standard `WebApplication.CreateBuilder` host defaults. To select a complete deployment-specific catalog, set `PipelineCatalogFile=/config/pipelinecatalog.json` or pass `--PipelineCatalogFile /config/pipelinecatalog.json`. Relative paths resolve against the host content root. Standard configuration precedence applies, with command line winning. This file replaces the bundled catalog; a missing selected file fails startup. Omitting a named entry from an override does not remove an earlier entry: select a complete file or explicitly disable that pipeline. Individual environment/CLI overrides use `PipelineCatalog__Pipelines__asd__...`. The selected catalog is the lowest-priority application source, below appsettings, user secrets, environment variables and command-line values. The catalog snapshot does not hot-reload; restart replicas after changes.

Each OpenShift deployment supplies its own pipeline list. All replicas of that deployment should receive the same settings. Integration and production use distinct per-pipeline indexes and destination descriptors; index names are never inferred from a namespace or environment label.

| Deployment | Pipeline ID | Rules index example |
|---|---|---|
| Integration | `asd` | `asd-integ-pipeline-index` |
| Production | `asd` | `asd-pipeline-index` |
| Integration | `algo` | `algo-integ-pipeline-index` |
| Production | `algo` | `algo-pipeline-index` |

These names are configuration examples; the foundation does not create indexes. Several pipeline IDs can select the same registered contract while retaining different indexes, extra data and destination descriptors.

A complete catalog example with a passive HTTP destination is:

```json
{
  "PipelineCatalog": {
    "RabbitMqConnections": {},
    "Pipelines": {
      "asd": {
        "Enabled": true,
        "ContractId": "asd",
        "RulesIndex": "asd-integ-pipeline-index",
        "ExtraData": {
          "origin": "integration",
          "options": { "enabled": true },
          "tags": ["example", 7, null]
        },
        "Transport": {
          "Kind": "http",
          "Http": {
            "Endpoint": "https://example.invalid/ingest?mode=integration",
            "Method": "POST",
            "TimeoutSeconds": 20,
            "Headers": { "Accept": "application/json" }
          }
        }
      }
    }
  }
}
```

HTTP descriptor fields are `Endpoint`, `Method`, `TimeoutSeconds` and `Headers`. Both HTTP and HTTPS URLs are accepted, including path and query. Fragments and embedded credentials are rejected. Configured framing/hop-by-hop headers such as `Host` and `Content-Length` are rejected; the payload contract owns content type. Timeout is stored and validated as metadata for the future sender. HTTP retry-policy fields are not part of this foundation.

For RabbitMQ, the alternative `Transport` value is:

```json
{
  "Kind": "rabbitmq",
  "RabbitMq": {
    "ConnectionRef": "asd-output",
    "Output": {
      "QueueName": "asd.output",
      "Arguments": {},
      "ExchangeSettings": {
        "ShouldBindToExchange": true,
        "ExchangeName": "asd.events",
        "ExchangeType": "topic",
        "RoutingKey": "asd.work",
        "Arguments": {},
        "BindingArguments": {}
      }
    }
  }
}
```

`ConnectionRef` selects an entry under `PipelineCatalog.RabbitMqConnections`. Each entry contains `Hostname`, `Port`, `Username`, `Password` and `VirtualHost`. Supply real credentials through deployment secrets. Multiple named broker descriptors are supported, but no connection is opened. Shared input configuration and input-to-output routing across different clusters belong to the Transport & Dispatch ticket.

Queue `Arguments` describe queue properties, exchange `Arguments` describe exchange properties, and `BindingArguments` describe exchange-to-queue binding criteria. A topic exchange usually uses the routing key with empty binding arguments. A headers exchange can use binding arguments such as `x-match`. The foundation accepts scalar argument values and normalizes scalar configuration strings where appropriate; nested argument tables are unsupported. An empty exchange describes the default-exchange queue route. Durability, exclusivity and auto-delete are not configurable fields. No topology is declared by this application.

## Payload preparation and extra data

`PipelineWorkPreparer.Prepare(pipelineId, context, runParams)` resolves the selected contract, checks enabled state, validates run parameters, per-pipeline `ExtraData` and contract input context, then builds `PreparedPipelineWork`. Unknown pipeline IDs fail lookup; disabled entries return no work. Invalid contract input returns field-level errors.

The caller supplies `PipelineDispatchContext`, including task/rule/image identifiers, ROI, acquisition time, sensor/image properties and grid metadata. The foundation does not retrieve that context or generate a matching result.

`OverlayDispatchContextFactory.Create(taskId, ruleId, overlayJson)` projects the flat update-overlay JSON (`id`, `photoTime`, `roiFootprint`, optional neighboring-image IDs and image metadata) into this context. It does not consume a queue or choose a rule. ASD validates its image/grid fields; Algo does not require ASD-only metadata. `footprint` is never substituted for `roiFootprint`. This projection only prepares fields used by the payload contracts; sensor/color inference, update-field filtering and other rule-engine normalization remain future work.

The factory keeps Algo's photographed `DateTime.Parse(photoTime, InvariantCulture)` behavior in `OverlayPhotoTime`, separately from the UTC `DateTimeOffset` used by ASD. Offset-bearing input may be converted to the host's local timezone by the legacy parse; use the same timezone for every replica when preserving that behavior. No new timezone policy is silently imposed on Algo.

The ASD run-parameter object is:

```json
{
  "tenantId": "tenant-example",
  "algorithmNames": ["FindAir"],
  "tilingConfigs": [
    {
      "tileSizeWidth": 500,
      "tileSizeHeight": 500,
      "tileOverlapWidth": 10,
      "tileOverlapHeight": 10
    }
  ]
}
```

The contract builds JSON bytes from context and run parameters. `PipelinePayload` also contains a content type, common string attributes and optional typed RabbitMQ attributes. ASD supplies tenant/algorithm attributes and preserves its existing integer `findair-contract-version` wire marker; that marker does not version the catalog's pipeline or contract ID. Mapping these attributes onto an actual request or broker message remains transport work.

`ExtraData` belongs to each pipeline in deployment configuration. Its JSON values are preserved, and the selected contract defines validation and use. ASD accepts an arbitrary object, writing a nonempty value under `extraData` without replacing existing fields or headers. Empty or omitted extra data preserves the existing ASD payload.

Algo requires the following case-sensitive settings in `ExtraData` (placeholder strings are intentional):

```json
{
  "XUserName": "replace-me-user",
  "Origin": "replace-me-origin",
  "QueueType": "replace-me-queue-type",
  "SaveDetections": true
}
```

The settings supply the body fields `origin`, `queueType`, `saveDetections`, and the null-only fallback for rule `username`. They are validated at startup, including for disabled pipelines. Algo does not append an `extraData` property or implicitly turn settings into headers. Several pipeline IDs may use `ContractId: "algo"` with different settings and destinations. See [the contracts library](../../libs/pipeline-contracts/README.md) for the complete Algo rule-parameter and output mapping.

The selected catalog file contains native `ExtraData` JSON objects. Standard host configuration files such as appsettings and user secrets must use a JSON string when overriding `ExtraData`; arbitrary native objects belong in the catalog file. Environment/configuration overrides replace the whole value with JSON text, for example:

```text
PipelineCatalog__Pipelines__asd__ExtraData={"origin":"integration","enabled":true}
```

Nested overrides such as `...__ExtraData__enabled` are rejected. Configuration sources retain normal precedence, but extra data is replaced as one object rather than merged field by field. The catalog is a startup snapshot; restart to apply a new configuration.

The old pipeline array format and nested `PipelineId` fields are rejected. Migrate deployment files to named objects and change indexed environment/CLI paths to pipeline IDs. IDs cannot differ only by case, contain `__`, or end with `_`; those spellings are ambiguous in environment-variable paths. Runtime lookups retain the configured ID spelling and remain case-sensitive. Overrides must use exactly the same spelling; case variants across providers fail startup.

## Rules

`PipelineRuleSnapshots` holds the active rules of every enabled pipeline. Each pipeline's rules are read from the index its catalog entry names, as v2 documents, and prepared with `libs/rule-engine`; run parameters are checked against the pipeline's contract. A rule that fails is left out and logged with its ID and reason (up to 10 per load).

Startup fails unless every enabled pipeline loads. After that, rules reload every `RuleRefresh:IntervalSeconds` (default 60) plus up to `RuleRefresh:JitterSeconds` (default 5). Each pipeline reloads on its own: a failed reload, or one where every rule was rejected, keeps that pipeline's previous rules and leaves the others untouched. Logs carry `pipeline.id` and counts as fields; `unified_gateway.rules.loads` counts loads by pipeline and outcome. Elasticsearch connection settings come from the `Elasticsearch` section.

## Dispatch

`PipelineDispatcher.DispatchAsync(units)` sends prepared work over each pipeline's transport, in parallel, and returns one outcome per unit in input order:

| Outcome | Meaning |
|---|---|
| `Delivered` | The transport confirmed delivery (RabbitMQ publisher confirm). |
| `Retryable` | Transient failure, such as a broker or connection error. Sending again may succeed. |
| `Rejected` | Sending again will not help, for example no transport or destination is configured for the unit. |

The dispatcher never throws for a failed unit; only cancellation propagates. Deciding what an outcome means for the source message belongs to the caller. A failed outcome carries a bounded error category (`TelemetryErrorCategory`, such as `timeout`, `connection`, `unavailable`, `dependency`, `validation` or `publish`), the HTTP status code when an endpoint answered, and the exception if there was one.

A `DispatchUnit` is prepared work plus:

- **`DispatchId`**: built by `DispatchIdentity.Create(imageId, pipelineId, ruleId, runParams)` as `{imageId}:{pipelineId}:{ruleId}:{hash}`. The hash covers the run parameters with object properties sorted, so the same match always yields the same ID. It is the downstream idempotency key: the AMQP `MessageId`, and later the HTTP `Idempotency-Key`.
- **`SourceMessageId`** and **`SourceHeaders`** from the consumed message. The source message ID becomes the AMQP `CorrelationId`.

Transports implement `IDispatchTransport` and are selected by the catalog's `Transport.Kind`. `RabbitMqDispatchTransport` resolves each enabled `rabbitmq` pipeline's output and named connection once, at startup, and publishes through `IRabbitMqDestinationPublisher` from `libs/rabbitmq-client`. The broker connection comes from the catalog only; the application needs no `RabbitMq` output settings. Published headers are the contract's string attributes, then its typed RabbitMQ attributes (which win on a name clash), plus the source's `findair-started-at-unix-ms`. Trace context is injected from the current span; no other source headers are forwarded.

`HttpDispatchTransport` sends each unit as one request to its enabled `http` pipeline's catalog `Endpoint`, with the catalog `Method`, from inside the message handler:

- **Body:** the payload bytes, with the contract's content type.
- **Headers:** the catalog `Headers`, then the contract's string attributes, then `Idempotency-Key: {DispatchId}`. Later entries win on a name clash. The receiving endpoint should deduplicate on `Idempotency-Key`, because a retried source message sends the same unit again.
- **Timeout:** `TimeoutSeconds` bounds the whole request, including connection setup. The shared `HttpClient` has no timeout of its own, and redirects are not followed.

| Result | Outcome |
|---|---|
| 2xx | `Delivered` |
| 408, 429, 5xx, timeout, connection failure | `Retryable` |
| Any other status, including 3xx | `Rejected` |

`Retry-After` is not honoured: a retryable unit makes the whole source message use the existing retry queues and their fixed delays. A slow endpoint holds a consumer slot for up to its timeout, so keep `TimeoutSeconds` short.

`IDispatchDeliveryListener` registrations are notified after each confirmed delivery, for example to record a unit as already processed. A listener failure is logged and does not change the outcome.

Telemetry for each unit is owned by `DispatchTelemetry`:

- **Span** `unified_gateway.dispatch` (stage `unified_gateway`): `Ok` when delivered; otherwise `Error` with `error.type` and `findair.error.category`. Tagged with `pipeline.id`, `pipeline.transport`, `findair.outcome` and `http.response.status_code` when present.
- **Metrics** `unified_gateway.dispatch.units` and `unified_gateway.dispatch.duration`, tagged with `pipeline.id`, `pipeline.transport`, `findair.outcome` and, on failure, `error.type`.
- **Logs** with static messages, so they group by message: 6001 delivered (debug), 6002 failed (warning, with the exception), 6003 delivery listener failed. One log scope per unit carries the dispatch ID (`messaging.message.id`), source message ID (`messaging.message.conversation_id`), `pipeline.id` and `pipeline.transport`; the failure log adds `findair.outcome`, `error.type` and `http.response.status_code`. These are the same field names as on the span and metrics, so a dead-lettered source message can be traced to the pipeline that failed by filtering, not by parsing text.

## Validation and observability

Startup rejects unknown catalog configuration fields, duplicate/invalid pipeline IDs, unregistered contracts, invalid rule-index names, unsupported or multiple transport descriptors, invalid connection references, invalid queue/exchange/header settings and non-object extra data. Disabled entries undergo the same validation. `RulesIndex` validation checks a lowercase literal index/alias name and its length/characters; it does not check index existence, Elasticsearch mappings or rule document fields.

The ASD contract rejects unknown/duplicate run-parameter fields, empty tenant/algorithm/tiling values, unsupported algorithms and invalid tile dimensions/overlaps. Algo validates its payload parameter types and original rule geometry, then converts that geometry to WKT. There is no geometry matching or spatial indexing stage here. Rule sensor, resolution and age conditions are not evaluated by either payload builder.

The host uses the same observability bootstrap as the regular gateway, with a distinct `unified-gateway` identity. Preparation records `unified_gateway.contract.preparations` and `unified_gateway.contract.preparation.duration`, tagged with configured pipeline ID and outcome, plus a `unified_gateway.contract.prepare` span. Logs report preparation results without dumping body data or credentials. Local settings enable console logs and Prometheus; OTLP and Logstash exporters are configured separately for deployment. No delivery/retry metrics are claimed because this application performs no delivery.

## Later tickets

The Rule Engine ticket owns Elasticsearch rule loading, validation of rule documents, matching and refreshable snapshots, with no spatial index. The rest of the Transport & Dispatch ticket covers input consumption, and acknowledgement, retry and dead-letter handling of source messages.

Retry reuses the existing per-message mechanism of `libs/rabbitmq-client`: if any unit is retryable, the whole source message is retried and matched again, so units that were already delivered are sent again with the same dispatch ID. A per-pipeline record of delivered units, plugged in through `IDispatchDeliveryListener`, can later skip them.
