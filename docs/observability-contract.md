# Imaging Pipeline observability contract

This document is the application-side contract for logs, traces, metrics, and
RabbitMQ propagation. Deployment examples are under `deploy/observability`.

## Signal routes

| Signal | Application output | Backend route |
|---|---|---|
| Traces | OTLP/gRPC or OTLP/HTTP | OpenTelemetry Collector → Fleet-managed Elastic APM → Elasticsearch |
| Logs | Batched ECS JSON over HTTP | Logstash → Elasticsearch logs data stream |
| Metrics | Prometheus text endpoint | Prometheus scrapes every application pod |

Metrics and logs are not sent through OTLP. Collector or Logstash reachability
is not checked during application startup. Missing or malformed required
configuration fails startup; a temporary remote outage does not stop business
processing.

## RabbitMQ wire metadata

### AMQP properties and publish arguments

Every publish performed by the shared client sets:

| Value | Behavior |
|---|---|
| `message_id` | Supplied by the application envelope |
| `correlation_id` | Supplied by the application envelope; normally the pipeline conversation ID |
| `content_type` | Supplied by the application envelope; application messages use `application/json` |
| `delivery_mode` | `2` through `Persistent = true` |
| `headers` | Cloned business headers plus the canonical telemetry/retry headers below |
| exchange | Supplied by the configured input/output/retry destination |
| routing key | Supplied by the configured destination |
| mandatory | `true` |
| body | The exact application payload bytes |

The client does not currently set `content_encoding`, AMQP `type`, `app_id`,
`user_id`, `reply_to`, `expiration`, `priority`, or the AMQP timestamp property.

`delivery_tag` and `redelivered` are broker delivery metadata, not application
headers.

### Application headers

| Header | Wire representation | Producer behavior |
|---|---|---|
| `traceparent` | UTF-8 bytes | Replaced with the current W3C producer context |
| `tracestate` | UTF-8 bytes | Replaced when the current trace has trace state; omitted otherwise |
| `baggage` | UTF-8 bytes | Rebuilt from the bounded allowlist described below |
| `x-pipeline-start-unix-ms` | signed integer milliseconds | Created at first pipeline entry and preserved across all stages |
| `x-pipeline-published-unix-ms` | signed integer milliseconds | Replaced immediately before every publish |
| `x-retry-count` | integer | `0` for normal output; incremented for delayed retries |
| `algorithm_name` | string/AMQP long string | Added only by TB Consumer output, for example `FindAir,Rpn` |

The baggage allowlist is:

- `tenant`
- `imaging_pipeline.task.id`
- `imaging_pipeline.request.id`
- `imaging_pipeline.image.id`
- `imaging_pipeline.rule.id`
- `imaging_pipeline.tenant.id`
- `imaging_pipeline.algorithm.name`

Baggage is limited to eight entries, 128 UTF-8 bytes per key, 256 UTF-8 bytes
per value, and 2 KiB in total. Message bodies, URLs, credentials, WKT,
coordinate arrays, and arbitrary customer metadata are never baggage.

Unknown incoming business headers are preserved. Header names used for W3C
propagation are canonicalized case-insensitively. Log serialization decodes
textual `byte[]`, `Memory<byte>`, and `ReadOnlyMemory<byte>` values as UTF-8;
non-text values are represented explicitly as bounded Base64 rather than
`System.Byte[]`.

### Per-stage behavior

| Stage | Outgoing message behavior |
|---|---|
| Gateway | Preserves business headers, sets pipeline start/publish time, resets retry count, injects trace context. Message ID is derived from input ID, rule, tenant, and output index. |
| TB Publisher | Preserves headers, resets retry count, refreshes publish time/trace context, and propagates validated task/image/rule/tenant/algorithm baggage. Each tile-building request has its own message ID and producer span but keeps the task trace ID. |
| External Tile Builder | Must preserve all received headers unchanged. Its internal spans are visible in the same waterfall only if it exports to the same Elastic APM destination. |
| TB Consumer | Preserves headers, adds `algorithm_name`, resets retry count, refreshes publish time/trace context, and propagates validated task/request/image/rule/tenant/algorithm baggage to Embedder messages. |
| Retry routing | Preserves body, IDs, content type, and business headers; increments `x-retry-count`; refreshes publish time and W3C context; preserves pipeline start time. |
| Dead letter | The client rejects without requeue. RabbitMQ may add `x-death`, `x-first-death-*`, and `x-last-death-*` headers. |

Retry queue topology uses queue arguments rather than message headers:
`x-message-ttl`, `x-dead-letter-exchange`, and
`x-dead-letter-routing-key`. Header-exchange bindings match
`x-retry-count=1`, `2`, and `3`.

## ECS log documents

Application log documents use the `findair` dataset. Each deployment
configuration explicitly selects `production`, `integration`, or `development`
as both its deployment environment and log data-stream namespace, producing
`logs-findair-production`, `logs-findair-integration`, or
`logs-findair-development`.

### Fields present on every application log

| ECS field | Source |
|---|---|
| `@timestamp` | UTC application timestamp |
| `ecs.version` | ECS schema version used by the serializer |
| `message` | Rendered `ILogger` message |
| `log.level` | Lowercase .NET log level |
| `log.logger` | Full logger category |
| `event.kind` | `event` |
| `event.code` | Numeric .NET `EventId` as a keyword |
| `event.action` | Event name or structured operation, when present |
| `event.dataset` | `findair` by default; configurable through `Observability:Logs:DataStream:Dataset` |
| `service.name` | Canonical service name |
| `service.namespace` | `imaging-pipeline` by default |
| `service.version` | Build/image version |
| `service.environment` | Deployment environment |
| `service.instance.id` | Pod UID, pod name, or process-unique fallback |
| `service.node.name` | Pod name, when available |
| `host.name` | Process host name |
| `process.pid` | Process ID |
| `process.thread.id` | Managed thread ID that emitted the record |
| `service.language.name` | `dotnet` |
| `service.language.version` | .NET runtime version |
| `service.runtime.name` | .NET runtime name |
| `service.runtime.version` | .NET runtime version |
| `data_stream.type` | `logs` |
| `data_stream.dataset` | `findair` by default; configurable per deployment |
| `data_stream.namespace` | Explicit deployment namespace: `production`, `integration`, or `development` |
| `kubernetes.*` | Namespace, pod, UID, deployment, node, and container when supplied by OpenShift |

When a log occurs inside a trace, it also contains `trace.id` and `span.id`.
Exceptions add `error.type`, `error.message`, and `error.stack_trace`.

Structured `TelemetryLogContext` values are flattened into:

- `messaging.message.id`
- `messaging.message.conversation_id`
- `messaging.destination.name`
- `imaging_pipeline.retry.attempt`
- `imaging_pipeline.task.id`
- `imaging_pipeline.request.id`
- `imaging_pipeline.image.id`
- `imaging_pipeline.rule.id`
- `imaging_pipeline.tenant.id`
- `imaging_pipeline.algorithm.name`

Known HTTP state is mapped to `http.request.method`,
`http.response.status_code`, `http.route` (the matched route template when
provided), `url.path`, and a query-free `url.full`.
Other bounded structured arguments are placed under `labels` using
snake_case names. `{OriginalFormat}` and rendered scope `ToString()` values are
not sent.

### Application event IDs

The metadata column lists event-specific values in addition to the common
fields and active telemetry scope.

| Event IDs | Component | Meaning | Event-specific metadata |
|---|---|---|---|
| `100`–`102` | RabbitMQ client | Consumer start/stop | destination, concurrency/index, batch size, prefetch |
| `103`–`104` | RabbitMQ client | Handler failure | message ID or batch count, exception |
| `105` | RabbitMQ client | Retry scheduled | message ID, retry attempt/limit, retry exchange |
| `106` | RabbitMQ client | Dead letter | message ID, dead-letter queue, bounded reason |
| `107` | RabbitMQ client | Completion/settlement failure | message ID, exception |
| `108`–`114` | RabbitMQ client | Connection lifecycle | role, host/port/vhost, broker shutdown code/initiator/reason, retry/disposal delay, exception where applicable |
| `2000`–`2003` | Gateway | Worker lifecycle/restart | restart delay and exception where applicable |
| `2010` | Gateway | Message processed (`Debug`) | rules evaluated/matched and output count |
| `2011`–`2012` | Gateway | Reject/retry | validation/error category and exception where applicable |
| `2013` | Gateway | Old-photo rule exclusion | filtered-rule count, image age, configured maximum age, photo time |
| `2020`–`2023` | Gateway | Rule-cache lifecycle | active/skipped/retained counts and bounded failed-rule sample |
| `3000`–`3003` | TB Publisher | Worker lifecycle/restart | restart delay and exception where applicable |
| `3010`–`3013` | TB Publisher | Validation/projection rejection | bounded validation reason and exception where applicable |
| `3014` | TB Publisher | Partial output publish | published and total counts |
| `3015` | TB Publisher | Message processed (`Debug`) | ground-point, tiling-config, and output counts |
| `4000`–`4003` | TB Consumer | Worker lifecycle/restart | restart delay and exception where applicable |
| `4010`–`4011` | TB Consumer | Input rejection | bounded validation/deserialization reason |
| `4013`–`4014`, `4016` | TB Consumer | Projection/output retry | tile/result/published counts |
| `4015` | TB Consumer | Message processed (`Debug`) | tile, mapped-coordinate, and output counts |
| `5001`–`5005` | Rules API | Operation start (`Debug`) | operation, rule ID, changed-field/count/activity metadata |
| `5010`–`5012` | Rules API | Conflict/not-found | operation, rule ID/count |
| `5020`–`5025` | Rules API | Operation result | operation, rule ID, field/request/success/failure counts, bounded failure sample |
| `5050`–`5051` | Rules API | Elasticsearch health failure | status/failure category and exception |
| `1001` | Rules API | HTTP model validation | HTTP method, matched route template, invalid-field/error counts, bounded field sample |
| `1002`–`1003` | Rules API | Persistence/unexpected HTTP failure | HTTP method, matched route template, exception |
| `0` | Framework/ad-hoc | Framework lifecycle or bounded internal diagnostic | Structured state supplied by that call |

Normal per-message success is `Debug`; lifecycle summaries are `Information`;
rejections/retries are `Warning`; exhausted or unrecoverable failures are
`Error`. Full bodies, raw Elasticsearch queries, PIT IDs, rule names, sensor
names, WKT, coordinates, credentials, signed query strings, and arbitrary
headers are not logged.

The HTTP exporter uses bounded normal and error-reserved queues. It sends JSON
arrays, retries only timeouts, HTTP 408/429, and 5xx responses with bounded
exponential backoff and jitter, and never blocks business processing on remote
I/O. Its own emergency output is rate-limited stderr to avoid recursive
logging.

## Trace contract

### Resource attributes

All spans carry the following resource identity when available:

- `service.name`
- `service.namespace`
- `service.version`
- `service.instance.id`
- `service.node.name`
- `deployment.environment` for Elasticsearch/Kibana 8.15 compatibility
- `deployment.environment.name` for newer semantic-convention compatibility
- `k8s.cluster.name`
- `k8s.namespace.name`
- `k8s.deployment.name`
- `k8s.pod.name`
- `k8s.pod.uid`
- `k8s.node.name`
- `k8s.container.name`
- `host.name`
- `process.pid`
- `process.runtime.name`
- `process.runtime.version`
- `process.runtime.description`

### Main waterfall

The expected trace is:

```text
Gateway RabbitMQ consume
  ├─ parse
  ├─ match
  ├─ build
  └─ RabbitMQ publish (one child producer span per output)
       └─ TB Publisher consume
            ├─ validate
            ├─ geometry
            ├─ projection
            │    └─ Projection Mapper HTTP client
            ├─ build
            └─ RabbitMQ publish (one child producer span per tile request)
                 └─ Tile Builder preserves W3C headers
                      └─ TB Consumer consume
                           ├─ validate
                           ├─ projection
                           │    └─ Projection Mapper HTTP client
                           ├─ build
                           └─ RabbitMQ publish to Embedder
```

All tiles for the same task retain the same `trace.id`. Each message hop has a
different producer/consumer `span.id`, which preserves causality without
collapsing independent tile operations.

RabbitMQ native spans use `messaging.system=rabbitmq`,
`messaging.destination.name`, and producer/consumer span kinds. Elasticsearch
uses client spans with `db.system=elasticsearch`,
`db.system.name=elasticsearch`, operation, index, server address/port, and
query-free URL metadata. Projection Mapper has a logical operation span and a
standard HTTP client dependency span.

Sampled application spans may contain task, request, image, rule, tenant, and
algorithm identifiers. These high-cardinality values are never metric labels.
Errors set span status, bounded `error.type`, and an exception event. Query
strings, bodies, WKT, coordinates, and credentials are excluded.

The default sampler is parent-based 100%. Every new root trace is sampled;
valid incoming W3C sampling decisions are respected.

## Metrics and labels

The names below are the .NET/OpenTelemetry instrument names. In Prometheus
exposition, the exporter replaces dots with underscores, appends normalized
unit suffixes such as `_seconds` or `_bytes`, and appends `_total` to monotonic
counters. Histogram series additionally use the normal `_bucket`, `_sum`, and
`_count` suffixes. Query the exposed names rather than copying the instrument
names verbatim into PromQL.

### Log transport

| Instrument | Labels |
|---|---|
| `imaging_pipeline.logs.dropped` | `imaging_pipeline.logs.drop.reason` |
| `imaging_pipeline.logs.export.requests` | `imaging_pipeline.pipeline.outcome`, optional `http.response.status_code_class` |
| `imaging_pipeline.logs.export.duration` | `imaging_pipeline.pipeline.outcome`, optional `http.response.status_code_class` |
| `imaging_pipeline.logs.queue.size` | none |

### RabbitMQ

| Instrument | Labels |
|---|---|
| `messaging.client.sent.messages` | system, destination, operation name/type, optional bounded error |
| `messaging.client.consumed.messages` | same |
| `messaging.client.operation.duration` | same; settlement adds outcome |
| `messaging.process.duration` | destination/operation, outcome, optional bounded error |
| `imaging_pipeline.messaging.message.body.size` | destination, operation |
| `imaging_pipeline.messaging.delivery.delay` | destination, consume operation |
| `imaging_pipeline.messaging.inflight` | destination, process operation |
| `imaging_pipeline.messaging.settlements` | destination, ack/nack operation, outcome, optional error |
| `imaging_pipeline.messaging.retries` | destination, process operation, outcome, optional error |
| `imaging_pipeline.rabbitmq.connections` | none |
| `imaging_pipeline.rabbitmq.connection.events` | bounded connection event, optional error |
| `imaging_pipeline.rabbitmq.channels` | publisher/consumer role |
| `imaging_pipeline.rabbitmq.publisher.channel_wait.duration` | none |
| `imaging_pipeline.rabbitmq.consumer.restarts` | optional bounded error |

### Pipeline and dependencies

| Instrument | Labels |
|---|---|
| `imaging_pipeline.pipeline.messages` | stage, direction, outcome, optional error |
| `imaging_pipeline.pipeline.payload.size` | stage, direction |
| `imaging_pipeline.pipeline.stage.duration` | stage, outcome, optional error |
| `imaging_pipeline.pipeline.external_stage.duration` | stage |
| `imaging_pipeline.pipeline.fanout` | stage |
| `imaging_pipeline.pipeline.batch.size` | stage, item |
| `imaging_pipeline.pipeline.end_to_end.duration` | stage |
| `imaging_pipeline.telemetry.invalid_timing_headers` | header kind, rejection reason |
| `imaging_pipeline.dependency.operations` | dependency, operation, outcome, optional bounded error |
| `imaging_pipeline.dependency.operation.duration` | same |
| `imaging_pipeline.dependency.payload.size` | dependency, operation, direction |
| `imaging_pipeline.dependency.batch.size` | dependency, operation, item |

### Gateway

| Instrument | Labels |
|---|---|
| `imaging_pipeline.gateway.rule_cache.entries` | none |
| `imaging_pipeline.gateway.rule_cache.age` | none |
| `imaging_pipeline.gateway.rule_cache.skipped_rules` | none |
| `imaging_pipeline.gateway.rule_cache.refreshes` | outcome, optional error |
| `imaging_pipeline.gateway.rule_cache.refresh.duration` | outcome, optional error |
| `imaging_pipeline.gateway.rules.evaluated` | none |
| `imaging_pipeline.gateway.rules.matched` | none |
| `imaging_pipeline.gateway.rules.filtered_photo_age` | none |

### Rules API

| Instrument | Labels |
|---|---|
| `imaging_pipeline.rules.operations` | operation, outcome, optional error |
| `imaging_pipeline.rules.operation.duration` | same |
| `imaging_pipeline.rules.documents` | same |
| `imaging_pipeline.rules.batch.size` | operation |
| `imaging_pipeline.rules.validation_failures` | operation, rejected outcome, validation error |

The exporter also exposes standard .NET runtime, ASP.NET Core, Kestrel, and
`HttpClient` instruments. Their labels are bounded protocol dimensions such as
HTTP method, route, status code, scheme, server address/port, and error type.

Prometheus discovery supplies target labels such as `job`, `instance`,
`service_name`, `service_version`, `deployment_environment`,
`k8s_namespace_name`, and `k8s_pod_name`.

Message, correlation, task, request, image, overlay, rule, tenant, and sensor
identifiers are forbidden metric labels. Raw URLs, exception messages, stack
traces, bodies, coordinates, and pod identity emitted from application
instruments are also forbidden. Pod identity belongs to Prometheus target
labels/resource metadata.
