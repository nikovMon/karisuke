# SPEC-001: Unified gateway foundation

Status: **Catalog & Contracts Foundation only**. The [application README](../apps/unified-gateway/README.md) describes the retained implementation and configuration. Transport & Dispatch and the Rule Engine ticket are separate future work. There is no deployment, index migration or production compatibility claim.

## Goal and current scope

The target is one gateway capable of preparing work for multiple pipelines, each with its own rules, parameters, payload contract and selected output transport. HTTP and RabbitMQ are the initial target protocols. This increment establishes the models and preparation boundary; it performs no broker or HTTP delivery.

The user's latest scope takes precedence over the [supplied specification and ticket screenshots](gateway-source-review.md):

- Create a separate Nx application under `apps/unified-gateway`.
- Use pipeline IDs in configuration and the metadata API; omit display names and pipeline version selectors.
- Select exactly one output transport descriptor per pipeline.
- Use a dedicated rule index for each pipeline in each deployment. Integration and production run separate deployments with explicit catalogs.
- Retain arbitrary optional per-pipeline `ExtraData` in deployment configuration.
- Keep pipeline metadata GET routes and foundation observability/tests.
- Leave the Rules API unchanged.
- Exclude the Rule Engine and Transport & Dispatch implementations. Add no spatial index.

## Components and ownership

| Component | Responsibility |
|---|---|
| `libs/pipeline-catalog` | Validate configuration and expose stable pipeline, connection and rule-source descriptors. |
| `libs/pipeline-contracts` | Register payload contracts, validate run parameters and build payload bytes/attributes. |
| `apps/unified-gateway` | Host metadata routes and observability; expose work preparation for an already matched pipeline. |
| Existing `apps/gateway` | Retain its current consumption, rule cache, matching and delivery behavior. |
| Existing `apps/rules-api` | Retain its current API and persistence behavior. |

The catalog stores deployment choices. A contract is a code implementation selected by `ContractId`, not a protocol sender. The same contract may be selected by several pipeline IDs with different rule sources, extra data and destinations.

The only implemented contract is `asd`. An Algo pipeline requires its actual contract implementation and registration before the catalog accepts it, even when configured as disabled.

## Catalog and deployment model

A pipeline contains `PipelineId`, `Enabled`, `ContractId`, `RulesIndex`, `ExtraData` and `Transport`. Pipeline IDs are unique and usable as a URL path segment. No environment flag derives index names.

| Deployment | Pipeline | Explicit index example |
|---|---|---|
| Integration | `asd` | `asd-integ-pipeline-index` |
| Production | `asd` | `asd-pipeline-index` |
| Integration, future Algo contract | `algo` | `algo-integ-pipeline-index` |
| Production, future Algo contract | `algo` | `algo-pipeline-index` |

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

`IPipelineContract` exposes contract identity, `ValidateRunParams` and `BuildPayload`. `PipelinePayload` contains serialized body bytes, content type, common string attributes and optional typed RabbitMQ attributes. The future sender will be responsible for protocol-specific request/message construction.

`PipelineWorkPreparer.Prepare(pipelineId, context, runParams)`:

1. Resolves the configured pipeline.
2. Returns `Disabled` with no work for a disabled entry.
3. Resolves its registered contract and validates supplied run parameters.
4. Returns `Invalid` with field-level errors, or builds `PreparedPipelineWork` using context, run parameters and that pipeline's `ExtraData`.

The caller must supply the context and run parameters. No matching, event normalization, rule lookup, dispatch identity generation or network operation happens during preparation.

The ASD contract validates `tenantId`, `algorithmNames` and `tilingConfigs`. It preserves the existing body field layout, tenant/algorithm attributes and integer AMQP wire marker when extra data is absent. Unsupported algorithm values from historical examples are not silently accepted. Input/rule identifiers and ROI are supplied by the caller rather than recomputed.

`ExtraData` is an optional JSON object with no predefined schema. JSON type, nesting, nulls and arrays are preserved. ASD writes nonempty values under `extraData`, preventing overrides of existing body fields; empty or omitted values preserve existing payload bytes. Extra data never becomes a header implicitly. Each deployment can supply a different object per pipeline. JSON-file handling preserves the object before ordinary .NET configuration flattening; overrides replace the whole object. The catalog remains a startup snapshot.

## Validation, API and observability

Startup validates catalog structure, pipeline ID uniqueness/format, registered contracts, literal rule-index names, one supported output descriptor, named broker references and descriptor fields. Disabled entries must also be valid. Unknown catalog configuration fields and malformed extra-data roots are rejected. HTTP headers reject invalid names/values and transport-managed framing fields. Queue/exchange names and scalar arguments are validated locally.

Rule-index validation covers naming only. There is no Elasticsearch connection or rule-document validation. Run-parameter validation belongs to contracts and is applied by the preparer; no new rule-authoring API exists.

`GET /pipelines` and `GET /pipelines/{pipelineId}` expose only ID, enabled state and contract ID; unknown IDs return 404. `/health` reports host/foundation state, and `/metrics` exposes Prometheus metrics. Neither endpoint proves downstream connectivity.

The application reuses the repository's observability bootstrap with a separate service identity. Contract preparation emits outcome counts, duration measurements, spans and structured logs. Configuration secrets and full payload values are excluded from metadata responses and preparation logs. Delivery/retry observability belongs to the later transport implementation.

Tests cover strict startup validation, registered/disabled contracts, safe metadata routes, catalog/configuration isolation, JSON-preserving extra data, ASD body compatibility and preparation outcomes. Test commands are listed in the application README; a passing unit suite does not establish downstream wire compatibility or a deployed topology.

## Future tickets and preserved decisions

The **Rule Engine** ticket owns rule documents, Elasticsearch loading, validation, in-memory snapshots, refresh failures and matching. It must preserve sensor/quality pairing and pipeline-specific semantics without adding a spatial index. The existing gateway's cache is not a unified-gateway cache implementation.

The **Transport & Dispatch** ticket owns shared source consumption, independent HTTP/RabbitMQ outputs, separate input/output brokers, connection/channel budgets, acknowledgements, publisher confirms, HTTP retries and external retry/DLQ processing. The source input schema, Algo payload contract and actual endpoint acceptance semantics still require fixtures.

The agreed external retry design is per outgoing unit: retry only failed work, preserving its body, headers, destination and stable dispatch ID. The retry path will not rematch rules. Successful siblings should not be deliberately replayed. A crash before source acknowledgement may redeliver an original event and rematch; preserving the first evaluation across that crash requires a durable inbox/dispatch plan. These remain design decisions for the transport/source integration work, not features of this foundation.

Rules API integration, rule migration, pipeline cutover and any production deployment require separate scoped work. The attached tickets do not by themselves authorize those operations.
