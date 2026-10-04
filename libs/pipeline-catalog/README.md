# Pipeline catalog

Shared configuration, contract registration validation, and rule-source resolution for the gateway and Rules API. This library performs no Elasticsearch, HTTP, or RabbitMQ I/O.

Register `IPipelineContractRegistry`, then call `services.AddPipelineCatalog(configuration)` in each host. `ValidateOnStart` checks the configuration even when no caller has resolved `IPipelineCatalog`. A missing catalog, empty pipeline object, unknown contract, or invalid entry fails startup with `OptionsValidationException`. Disabled entries undergo the same validation; registered contracts that are not configured are allowed.

## Shared JSON file

[`configuration/pipelinecatalog.json`](configuration/pipelinecatalog.json) holds the shared sample catalog, with ASD and Algo pipeline entries. It is separate from host-specific observability/logging settings. The library copies it to consuming build/publish output as `pipelinecatalog.json` so it works without a repository checkout at runtime. Values for Algo are placeholders; deployments supply their own values and credentials.

`AddPipelineCatalogJsonFile(path)` is a reusable configuration extension that preserves `ExtraData` JSON types before the first load, including when used on a live `ConfigurationManager`. A minimal loading sequence for a future host is:

```csharp
var configuration = new ConfigurationBuilder()
    .AddPipelineCatalogJsonFile(Path.Combine(AppContext.BaseDirectory, "pipelinecatalog.json"))
    .AddEnvironmentVariables()
    .AddCommandLine(args)
    .Build();
```

Register the contracts named in the selected file, then call `services.AddPipelineCatalog(configuration)`. There is no automatic Rules API integration. Select one complete catalog file for the host's deployment: named entries merge across configuration providers, and omission does not remove an earlier entry. The unified gateway selects a replacement path using `PipelineCatalogFile` with normal host configuration precedence. The file is required by default and the library does not watch it unless explicitly requested; the catalog itself remains a startup snapshot.

Each gateway deployment provides its own pipeline list. Every entry declares its rule index, contract and transport. The integration and production deployments keep the same target pipeline IDs, with a dedicated index in each environment: ASD uses `asd-integ-pipeline-index` in integration and `asd-pipeline-index` in production; Algo follows the same pattern. The catalog does not infer routing from the OpenShift namespace, .NET environment, or pipeline name.

Unknown configuration keys fail binding at startup, including nested `PipelineId`, removed `DisplayName`, global `Environment`/`IntegrationRulesIndex`, and per-entry `ProductionRulesIndex` settings. `Pipelines` is an object keyed by pipeline ID, with `PipelineSettings` values; the old array shape is rejected. The catalog populates runtime `PipelineDefinition.PipelineId` from the key. Contract IDs have no version suffix.

```json
{
  "PipelineCatalog": {
    "RabbitMqConnections": {
      "asd-output": {
        "Hostname": "localhost",
        "Port": 5672,
        "Username": "guest",
        "Password": "guest",
        "VirtualHost": "/"
      }
    },
    "Pipelines": {
      "asd": {
        "Enabled": true,
        "ContractId": "asd",
        "RulesIndex": "asd-integ-pipeline-index",
        "ExtraData": {},
        "Transport": {
          "Kind": "rabbitmq",
          "RabbitMq": {
            "ConnectionRef": "asd-output",
            "Output": {
              "QueueName": "publisher",
              "Arguments": {},
              "ExchangeSettings": {
                "ShouldBindToExchange": false,
                "ExchangeName": "",
                "ExchangeType": "direct",
                "RoutingKey": "",
                "Arguments": {},
                "BindingArguments": {}
              }
            }
          }
        }
      }
    }
  }
}
```

Pipeline/contract lookups are case-sensitive. Pipeline keys cannot differ only by case, contain `__`, or end with `_`; these spellings are ambiguous in configuration/environment paths. Use the same ID spelling in every provider; case-variant overrides are rejected before binding, including across chained configuration sources. Transport `Kind` accepts exactly `rabbitmq` or `http`, with only the matching settings object. Each pipeline has exactly one output transport descriptor. HTTP settings are `Endpoint` (full HTTP/HTTPS URL including query), `Method` (default `POST`), positive `TimeoutSeconds` (default `20`), and `Headers` (name/value dictionary). Header names and values are validated; request framing and contract-owned content type cannot be overridden. The contract builds body bytes and content type; transport configuration does not contain a body template. These settings describe a destination without sending requests or implementing delivery policies.

`ExtraData` is the per-pipeline contract-settings object, defaulting to `{}`. Strings, numbers, booleans, nulls, arrays and nested objects retain their JSON types, property names and empty containers. The catalog calls the selected contract's `ValidateExtraData` at startup. ASD allows arbitrary values and retains its nested body behavior; Algo requires `XUserName`, `Origin`, `QueueType` and boolean `SaveDetections`, mapping them to its known body fields. There is no separate `ContractSettings` section. Use `AddPipelineCatalogJsonFile` for catalog files or `AddPipelineCatalogJsonStream` for streams. The unified host uses standard `WebApplication.CreateBuilder`: native `ExtraData` objects belong in the dedicated catalog, while appsettings/user-secrets overrides must supply a complete JSON string. `PreservePipelineExtraDataJson` remains available for other hosts that explicitly stage their JSON sources before loading. A complete JSON-string override at `PipelineCatalog__Pipelines__asd__ExtraData` replaces the whole object according to normal provider precedence; hierarchical overrides below `ExtraData` are rejected because ordinary configuration binding loses JSON types.

`RabbitMqConnections` holds reusable broker definitions with hostname, port, username, password and virtual host. `IRabbitMqConnectionResolver.GetRequired(connectionRef)` resolves a validated definition. Unknown output references fail startup. Passwords must come from deployment secrets outside the local sample and are not included in validation errors. This foundation exposes no pipeline metadata endpoints.

Queue settings include name, scalar arguments and exchange settings. Exchange settings include binding flag, name, type, routing key, arguments and binding arguments. Binding is explicit; output publication to a named exchange does not require this service to create a binding. The default exchange routes output by queue name. Configuration strings representing numbers/booleans are normalized for RabbitMQ arguments. Known string fields (`x-dead-letter-exchange`, `x-dead-letter-routing-key`, `x-queue-type`, `x-overflow`, `x-match`) remain strings, including numeric-looking values. Nested AMQP argument tables are not supported. These models resolve settings without establishing connections or executing topology operations.

Queue `Arguments` configure queue behavior such as message TTL and dead-letter routing. `ExchangeSettings.Arguments` configure exchange declaration options. `ExchangeSettings.BindingArguments` configure matching criteria on the binding, especially for headers exchanges where `x-match` selects `all` or `any`. A topic binding using `RoutingKey: "#"` normally has empty binding arguments (`{}`).

Conversion delegates to the dependency-free [RabbitMQ configuration library](../rabbitmq-configuration/README.md) with the catalog's extended numeric policy. Scalar validation remains here, and normalization happens once when the catalog snapshot is created; public reads only copy the normalized values.

`IPipelineCatalog.GetAll()` includes disabled entries for administration; `GetEnabled()` excludes them for dispatch selection. Configuration changes require restarting the host.

`IRuleSourceResolver.Resolve(pipelineId)` returns that entry's configured `RulesIndex` as `IndexName`, together with `PipelineId`. Consumers must use both values and validate pipeline ownership on reads and mutations; this resolver does not itself execute or enforce an Elasticsearch filter. Pipeline IDs must be unique within a deployment's catalog. Index names are explicit deployment settings, not generated by the library.

One catalog entry selects one contract and one transport configuration. Integration can exercise several target services using separate entries, each with its own index and RabbitMQ or HTTP destination. Adding a future contract ID also requires its implementation to be registered; configuration alone does not create it.

Literal index names/aliases are accepted; wildcard, remote-cluster, multi-index, and date-math expressions are rejected. Aliases still need correct deployment ownership because their physical targets are not inspected here.
