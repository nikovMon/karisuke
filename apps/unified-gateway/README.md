# Unified gateway: Catalog & Contracts Foundation

This Nx application implements the **Catalog & Contracts Foundation** ticket. It hosts a validated pipeline catalog and prepares contract payloads for an already matched pipeline. The existing gateway and Rules API retain their current behavior.

**Transport & Dispatch is outside this change.** The application has no RabbitMQ consumer/publisher, HTTP sender/request factory, connection/channel pool, dispatch coordinator, retry worker or retry/DLQ topology. HTTP and RabbitMQ output settings are passive catalog descriptors. Starting the application does not open broker connections or contact downstream endpoints.

## Retained features

| Feature | Current behavior |
|---|---|
| Nx application | Separate `apps/unified-gateway` application with build, run, publish and Docker targets. |
| Shared catalog | `libs/pipeline-catalog` provides pipeline lookup, enabled entries, named RabbitMQ connection resolution and rule-source resolution. Runtime values are copied into a stable catalog; callers cannot change it by mutating returned collections. |
| Pipeline identity | `PipelineId` identifies the pipeline in configuration and metadata routes. No display name or pipeline version field. |
| Contract selection | `ContractId` selects a registered implementation that validates run parameters and builds payload bytes and attributes. It is independent of transport choice. |
| Rule-source descriptor | Each pipeline has an explicit `RulesIndex`. `IRuleSourceResolver` returns its index and pipeline ID without querying Elasticsearch. |
| Output descriptor | Exactly one `http` or `rabbitmq` descriptor per pipeline, with validated settings. |
| ASD contract | The registered `asd` contract validates tenant, algorithm and tiling parameters and builds the existing ASD JSON body and attributes. No Algo contract is registered yet. |
| Extra data | Optional arbitrary JSON object per pipeline, preserved without converting its JSON values into strings. ASD includes nonempty values under the body property `extraData`. |
| Work preparation | `PipelineWorkPreparer` receives a pipeline ID, event context and supplied run parameters, then returns `Prepared`, `Disabled` or `Invalid`. It never matches rules or performs delivery. |
| Metadata routes | `GET /pipelines` and `GET /pipelines/{pipelineId}` expose pipeline ID, enabled state and contract ID. Disabled entries remain visible; unknown IDs return 404. |
| Observability | Shared host logs, traces, runtime/ASP.NET metrics, Prometheus scraping and preparation-specific logs, spans and metrics. |
| Tests | Catalog/contract validation, isolated configuration values, ASD compatibility, preparation outcomes and metadata-route behavior. |

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

The checked-in Prometheus listener uses port **9464**, serving `/health`, `/pipelines`, `/pipelines/{pipelineId}` and `/metrics`. `/health` identifies the foundation capabilities and inactive dispatch workflow. It does not check broker or Elasticsearch connectivity. Change `Observability__Metrics__Prometheus__Port` when another local service uses that port.

## Deployment configuration

The application configuration contains `PipelineCatalog`, `Observability` and standard .NET host/logging settings. There is no active `Gateway.Input`, `Gateway.Retry` or `Gateway.RabbitMq` runtime configuration.

Each OpenShift deployment supplies its own pipeline list. All replicas of that deployment should receive the same settings. Integration and production use distinct per-pipeline indexes and destination descriptors; index names are never inferred from a namespace or environment label.

| Deployment | Pipeline ID | Rules index example |
|---|---|---|
| Integration | `asd` | `asd-integ-pipeline-index` |
| Production | `asd` | `asd-pipeline-index` |
| Integration, after an Algo contract is added | `algo` | `algo-integ-pipeline-index` |
| Production, after an Algo contract is added | `algo` | `algo-pipeline-index` |

These names are configuration examples; the foundation does not create indexes. Several pipeline IDs can select the same registered contract while retaining different indexes, extra data and destination descriptors.

A complete catalog example with a passive HTTP destination is:

```json
{
  "PipelineCatalog": {
    "RabbitMqConnections": {},
    "Pipelines": [
      {
        "PipelineId": "asd",
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
    ]
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

`PipelineWorkPreparer.Prepare(pipelineId, context, runParams)` resolves the selected contract, checks enabled state, validates the supplied run parameters and builds `PreparedPipelineWork`. Unknown pipeline IDs fail lookup; disabled entries return no work. Invalid run parameters return field-level errors.

The caller supplies `PipelineDispatchContext`, including task/rule/image identifiers, ROI, acquisition time, sensor/image properties and grid metadata. The foundation does not retrieve that context or generate a matching result.

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

`ExtraData` belongs to each pipeline in deployment configuration. It defaults to `{}` and supports arbitrary nested objects, arrays, strings, numbers, booleans and null values. The ASD contract writes a nonempty object under `extraData`, so it cannot replace existing body fields or become headers. Empty or omitted extra data preserves the existing ASD payload. Adding it changes the outgoing body shape; each receiving service must support that field when sending is implemented.

The application uses the catalog's JSON configuration support to retain JSON types. JSON files contain a real object. Environment/configuration overrides replace the whole value with JSON text, for example:

```text
PipelineCatalog__Pipelines__0__ExtraData={"origin":"integration","enabled":true}
```

Nested overrides such as `...__ExtraData__enabled` are rejected. Configuration sources retain normal precedence, but extra data is replaced as one object rather than merged field by field. The catalog is a startup snapshot; restart to apply a new configuration. Metadata routes never expose extra-data values, credentials, index names or endpoints.

## Validation and observability

Startup rejects unknown catalog configuration fields, duplicate/invalid pipeline IDs, unregistered contracts, invalid rule-index names, unsupported or multiple transport descriptors, invalid connection references, invalid queue/exchange/header settings and non-object extra data. Disabled entries undergo the same validation. `RulesIndex` validation checks a lowercase literal index/alias name and its length/characters; it does not check index existence, Elasticsearch mappings or rule document fields.

The ASD contract rejects unknown/duplicate run-parameter fields, empty tenant/algorithm/tiling values, unsupported algorithms and invalid tile dimensions/overlaps. Geometry is supplied as a GeoJSON object; there is no geometry matching or spatial indexing stage here.

The host uses the same observability bootstrap as the regular gateway, with a distinct `unified-gateway` identity. Preparation records `unified_gateway.contract.preparations` and `unified_gateway.contract.preparation.duration`, tagged with configured pipeline ID and outcome, plus a `unified_gateway.contract.prepare` span. Logs report preparation results without dumping body data or credentials. Local settings enable console logs and Prometheus; OTLP and Logstash exporters are configured separately for deployment. No delivery/retry metrics are claimed because this application performs no delivery.

## Later tickets

The Rule Engine ticket owns Elasticsearch rule loading, validation of rule documents, matching and refreshable snapshots, with no spatial index. The Transport & Dispatch ticket owns input consumption, HTTP/RabbitMQ sends, cross-cluster routing, channel pools, acknowledgements, internal HTTP retries and external retry/DLQ processing.

The retained retry design decision for that later ticket is to queue **only failed outgoing units**, preserving their prepared body, headers, destination and dispatch ID. A retry resends saved work without matching rules again. This is a future design decision, not an implemented worker or delivery guarantee in this foundation.
