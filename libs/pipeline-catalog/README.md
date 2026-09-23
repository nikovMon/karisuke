# Pipeline catalog

Shared configuration, contract registration validation, and rule-source resolution for the gateway and Rules API. This library performs no Elasticsearch, HTTP, or RabbitMQ I/O.

Register `IPipelineContractRegistry`, then call `services.AddPipelineCatalog(configuration)` in each host. `ValidateOnStart` checks the configuration even when no caller has resolved `IPipelineCatalog`. A missing catalog, empty pipeline list, unknown contract, or invalid entry fails startup with `OptionsValidationException`. Disabled entries undergo the same validation; registered contracts that are not configured are allowed.

Each gateway deployment provides its own pipeline list. Every entry declares its rule index, contract and transport. The integration and production deployments keep the same target pipeline IDs, with a dedicated index in each environment: ASD uses `asd-integ-pipeline-index` in integration and `asd-pipeline-index` in production; Algo follows the same pattern. The catalog does not infer routing from the OpenShift namespace, .NET environment, or pipeline name.

Unknown configuration keys fail binding at startup, including removed `DisplayName`, global `Environment`/`IntegrationRulesIndex`, and per-entry `ProductionRulesIndex` settings. Use `PipelineId` as the API identity and `RulesIndex` on each entry. Contract IDs have no version suffix.

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
    "Pipelines": [
      {
        "PipelineId": "asd",
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
    ]
  }
}
```

Pipeline/contract identifiers are case-sensitive. Transport `Kind` accepts exactly `rabbitmq` or `http`, with only the matching settings object. Each pipeline has exactly one output transport descriptor. HTTP settings are `Endpoint` (full HTTP/HTTPS URL including query), `Method` (default `POST`), positive `TimeoutSeconds` (default `20`), and `Headers` (name/value dictionary). Header names and values are validated; request framing and contract-owned content type cannot be overridden. The contract builds body bytes and content type; transport configuration does not contain a body template. These settings describe a destination without sending requests or implementing delivery policies.

`ExtraData` is an optional JSON object per pipeline, defaulting to `{}`. Strings, numbers, booleans, nulls, arrays and nested objects retain their JSON types, property names and empty containers. The catalog supplies this object to the selected contract. Use `PreservePipelineExtraDataJson` on staged JSON file sources before their first load, or `AddPipelineCatalogJsonStream` for streams. A complete JSON-string override at `PipelineCatalog__Pipelines__0__ExtraData` replaces the whole object according to normal provider precedence; hierarchical overrides below `ExtraData` are rejected because ordinary configuration binding loses JSON types.

`RabbitMqConnections` holds reusable broker definitions with hostname, port, username, password and virtual host. `IRabbitMqConnectionResolver.GetRequired(connectionRef)` resolves a validated definition. Unknown output references fail startup. Passwords must come from deployment secrets outside the local sample and are not returned by metadata endpoints or included in validation errors.

Queue settings include name, scalar arguments and exchange settings. Exchange settings include binding flag, name, type, routing key, arguments and binding arguments. Binding is explicit; output publication to a named exchange does not require this service to create a binding. The default exchange routes output by queue name. Configuration strings representing numbers/booleans are normalized for RabbitMQ arguments. Known string fields (`x-dead-letter-exchange`, `x-dead-letter-routing-key`, `x-queue-type`, `x-overflow`, `x-match`) remain strings, including numeric-looking values. Nested AMQP argument tables are not supported. These models resolve settings without establishing connections or executing topology operations.

Queue `Arguments` configure queue behavior such as message TTL and dead-letter routing. `ExchangeSettings.Arguments` configure exchange declaration options. `ExchangeSettings.BindingArguments` configure matching criteria on the binding, especially for headers exchanges where `x-match` selects `all` or `any`. A topic binding using `RoutingKey: "#"` normally has empty binding arguments (`{}`).

`IPipelineCatalog.GetAll()` includes disabled entries for administration; `GetEnabled()` excludes them for dispatch selection. Configuration changes require restarting the host.

`IRuleSourceResolver.Resolve(pipelineId)` returns that entry's configured `RulesIndex` as `IndexName`, together with `PipelineId`. Consumers must use both values and validate pipeline ownership on reads and mutations; this resolver does not itself execute or enforce an Elasticsearch filter. Pipeline IDs must be unique within a deployment's catalog. Index names are explicit deployment settings, not generated by the library.

One catalog entry selects one contract and one transport configuration. Integration can exercise several target services using separate entries, each with its own index and RabbitMQ or HTTP destination. Adding a future contract ID also requires its implementation to be registered; configuration alone does not create it.

Literal index names/aliases are accepted; wildcard, remote-cluster, multi-index, and date-math expressions are rejected. Aliases still need correct deployment ownership because their physical targets are not inspected here.
