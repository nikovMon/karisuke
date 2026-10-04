# SPEC-001: Unified gateway foundation

Status: **Catalog & Contracts Foundation only**. The [application README](../apps/unified-gateway/README.md) describes the retained implementation and configuration. Transport & Dispatch and the Rule Engine ticket are separate future work. There is no deployment, index migration or production compatibility claim.

## Goal and current scope

The target is one gateway capable of preparing work for multiple pipelines, each with its own rules, parameters, payload contract and selected output transport. HTTP and RabbitMQ are the initial target protocols. This increment establishes the models and preparation boundary; it performs no broker or HTTP delivery.

The user's latest scope takes precedence over the [supplied specification and ticket screenshots](gateway-source-review.md):

- Create a separate Nx application under `apps/unified-gateway`.
- Key pipeline configuration by ID; omit redundant nested IDs, display names and pipeline version selectors.
- Select exactly one output transport descriptor per pipeline.
- Use a dedicated rule index for each pipeline in each deployment. Integration and production run separate deployments with explicit catalogs.
- Use per-pipeline `ExtraData` as contract settings, preserving arbitrary JSON types and contract-specific validation.
- Keep the catalog JSON and reusable loader under `libs/pipeline-catalog` for future host reuse; do not integrate the Rules API yet.
- Remove pipeline metadata routes; retain health, metrics and foundation tests.
- Leave the Rules API unchanged.
- Exclude the Rule Engine and Transport & Dispatch implementations. Add no spatial index.

## Components and ownership

| Component | Responsibility |
|---|---|
| `libs/pipeline-catalog` | Validate configuration and expose stable pipeline, connection and rule-source descriptors. |
| `libs/pipeline-contracts` | Register payload contracts, validate run parameters and build payload bytes/attributes. |
| `libs/rabbitmq-configuration` | Share argument normalization without broker or host dependencies; keep validation in the caller. |
| `apps/unified-gateway` | Host health/metrics and observability; expose work preparation for an already matched pipeline. |
| Existing `apps/gateway` | Retain its current consumption, rule cache, matching and delivery behavior. |
| Existing `apps/rules-api` | Retain its current API and persistence behavior. |

The catalog stores deployment choices. A contract is a code implementation selected by `ContractId`, not a protocol sender. The same contract may be selected by several pipeline IDs with different rule sources, extra data and destinations.

Implemented contracts are `asd` and `algo`. The Algo mission-body mapping comes from the supplied September 23 code screenshots. Roberto and warmup are excluded. Transport execution and downstream response handling remain outside this foundation.

## Catalog and deployment model

`PipelineCatalog.Pipelines` is an object keyed by ID. Each value contains `Enabled`, `ContractId`, `RulesIndex`, `ExtraData` and `Transport`; runtime descriptors add `PipelineId` from the key. Arrays and nested IDs are rejected. IDs are URL-safe, unique ignoring case, and cannot contain `__` or end with `_`, so environment-variable paths remain unambiguous. Runtime lookups remain case-sensitive; overrides must preserve the configured key spelling. Case variants across providers and scalar overrides of pipeline objects fail before binding. No environment flag derives index names.

The reusable sample is `libs/pipeline-catalog/configuration/pipelinecatalog.json`, copied into consuming build and publish outputs. The gateway uses standard `WebApplication.CreateBuilder`, then inserts the selected catalog below standard application configuration sources. `AddPipelineCatalogJsonFile` preserves JSON values. `PipelineCatalogFile` selects one complete replacement file; environment and command-line settings follow standard precedence. Native `ExtraData` JSON objects belong in this catalog. Host files and environment/CLI overrides use a complete JSON string at a named key such as `PipelineCatalog__Pipelines__asd__ExtraData`. Omission in a partial override does not delete an existing entry. Future Rules API use requires separate integration work.

| Deployment | Pipeline | Explicit index example |
|---|---|---|
| Integration | `asd` | `asd-integ-pipeline-index` |
| Production | `asd` | `asd-pipeline-index` |
| Integration | `algo` | `algo-integ-pipeline-index` |
| Production | `algo` | `algo-pipeline-index` |

Each deployment supplies the full catalog to every replica. A separate integration deployment can describe different downstream services while retaining the same pipeline IDs. `IRuleSourceResolver` returns `(IndexName, PipelineId)` for later storage callers. It does not query Elasticsearch, load rules or verify mappings/ownership. No index is created or migrated.

`PipelineCatalog.RabbitMqConnections` holds named broker descriptors. RabbitMQ outputs reference one using `ConnectionRef`. This separates credentials/host settings from each pipeline entry and permits multiple destinations. The foundation resolves these values without opening connections.

## Passive output descriptors

| Descriptor | Fields retained |
|---|---|
| HTTP | Full HTTP(S) `Endpoint`, `Method`, `TimeoutSeconds`, string `Headers`. |
| RabbitMQ | `ConnectionRef`, `Output.QueueName`, scalar queue `Arguments`, `ExchangeSettings` with bind flag, exchange name/type, routing key, exchange arguments and binding arguments. |
| Broker connection | `Hostname`, `Port`, `Username`, `Password`, `VirtualHost`. |

Exactly one descriptor must match `Transport.Kind`. These fields describe intended delivery and are validated without sending or declaring topology. HTTP internal retry policies and all `Gateway.Input`, `Gateway.Retry` and `Gateway.RabbitMq` runtime settings are outside this increment. There is no HTTP request factory, RabbitMQ channel pool or transport interface implementation in the foundation.

## Contract and preparation behavior

`IPipelineContract` exposes contract identity, `ValidateRunParams`, `ValidateExtraData`, `ValidateContext` and `BuildPayload`. `PipelinePayload` contains serialized body bytes, content type, common string attributes and optional typed RabbitMQ attributes. The future sender will be responsible for protocol-specific request/message construction.

`PipelineWorkPreparer.Prepare(pipelineId, context, runParams)`:

1. Resolves the configured pipeline.
2. Returns `Disabled` with no work for a disabled entry.
3. Resolves its registered contract and validates supplied run parameters, settings and context.
4. Returns `Invalid` with field-level errors, or builds `PreparedPipelineWork` using context, run parameters and that pipeline's `ExtraData`.

The caller must supply the context and run parameters. An explicit `OverlayDispatchContextFactory` can project the flat overlay JSON beforehand, preserving Algo's legacy parsed photo timestamp and nullable neighboring-image IDs. No rule matching, rule lookup, dispatch identity generation or network operation happens during preparation.

The ASD contract validates `tenantId`, `algorithmNames` and `tilingConfigs`. It preserves the existing body field layout, tenant/algorithm attributes and integer AMQP wire marker when extra data is absent. Unsupported algorithm values from historical examples are not silently accepted. Input/rule identifiers and ROI are supplied by the caller rather than recomputed.

`ExtraData` is the contract-settings JSON object, not a separate configuration section. Its meaning is contract-specific. ASD writes arbitrary nonempty values under `extraData`; empty or omitted values preserve existing payload bytes. Algo requires `XUserName`, `Origin`, `QueueType` and boolean `SaveDetections`, mapped to known body fields with no nested extra-data output. It uses rule `username` unless it is null. Settings never become a header implicitly. Each pipeline can supply a different object. JSON-file handling preserves the object before ordinary .NET configuration flattening; overrides replace the whole object. The catalog remains a startup snapshot.

Algo prepares one mission request with a single task for the supplied rule. It preserves nullable `profile_name`, `hebrew_rule_name`, `username` and task relationship fields. The two rule flags control task field assignment. `missionName` contains the rule's Hebrew name and `dd/MM/yyyy` photo date; `focusedWkt` comes from the original rule `location_geojson`, never the overlay/intersection ROI. Algorithm names are strings independent of ASD's enum. See the contracts README for input examples and compatibility tests. The HTTP descriptor specifies PUT, but no request is sent.

## Validation, API and observability

Startup validates catalog structure, pipeline ID uniqueness/format, registered contracts, literal rule-index names, one supported output descriptor, named broker references and descriptor fields. Disabled entries must also be valid. Unknown catalog configuration fields and malformed extra-data roots are rejected. HTTP headers reject invalid names/values and transport-managed framing fields. Queue/exchange names and scalar arguments are validated locally.

Rule-index validation covers naming only. There is no Elasticsearch connection or rule-document validation. Run-parameter validation belongs to contracts and is applied by the preparer; no new rule-authoring API exists.

Pipeline metadata routes are removed from the gateway and are not added to the Rules API. `/health` reports host/foundation state, and `/metrics` exposes Prometheus metrics. Neither endpoint proves downstream connectivity. The catalog remains accessible in-process through its existing interfaces.

The application reuses the repository's observability bootstrap with a separate service identity. Contract preparation emits outcome counts, duration measurements, spans and structured logs. Configuration secrets and full payload values are excluded from preparation logs. Delivery/retry observability belongs to the later transport implementation.

Tests cover strict startup validation, registered/disabled contracts, absence of metadata routes, named configuration overrides, catalog isolation, JSON-preserving extra data, ASD body compatibility and preparation outcomes. Shared RabbitMQ normalization tests cover string-valued arguments, culture-independent parsing, existing typed values and actual declaration/binding arguments. Test commands are listed in the application README; a passing unit suite does not establish downstream wire compatibility or a deployed topology.

## Future tickets and preserved decisions

The **Rule Engine** ticket owns rule documents, Elasticsearch loading, validation, in-memory snapshots, refresh failures and matching. It must preserve sensor/quality pairing and pipeline-specific semantics without adding a spatial index. The existing gateway's cache is not a unified-gateway cache implementation.

The **Transport & Dispatch** ticket owns shared source consumption, independent HTTP/RabbitMQ outputs, separate input/output brokers, connection/channel budgets, acknowledgements, publisher confirms, HTTP retries and external retry/DLQ processing. Flat overlay and Algo payload code examples are now available; actual endpoint acceptance, authentication and idempotency still require integration verification.

The agreed external retry design is per outgoing unit: retry only failed work, preserving its body, headers, destination and stable dispatch ID. The retry path will not rematch rules. Successful siblings should not be deliberately replayed. A crash before source acknowledgement may redeliver an original event and rematch; preserving the first evaluation across that crash requires a durable inbox/dispatch plan. These remain design decisions for the transport/source integration work, not features of this foundation.

Rules API integration, rule migration, pipeline cutover and any production deployment require separate scoped work. The attached tickets do not by themselves authorize those operations.
