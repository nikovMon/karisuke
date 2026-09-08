# FindAir observability contract

This is the application-side contract for logs, traces, metrics, and RabbitMQ
propagation. Deployment examples are under deploy/observability.

This document defines which signals must exist.
[pipeline-telemetry-pattern.md](pipeline-telemetry-pattern.md) defines how
handler code emits them, and is the guide to follow when migrating a service.

## Signal routes

| Signal | Application output | Backend route |
|---|---|---|
| Traces | OTLP/gRPC or OTLP/HTTP | OpenTelemetry Collector -> Fleet-managed Elastic APM -> Elasticsearch |
| Logs | Batched ECS JSON over HTTP | Existing environment Logstash -> logs-findair-{environment} |
| Metrics | Prometheus endpoint | Prometheus scrapes every application pod |

Metrics and logs are not sent through OTLP. Missing or malformed required
configuration fails startup. A temporary Collector or Logstash outage does not
stop business processing.

Tile-delivery events remain in the normal application log data stream. A
separate tile-delivery dataset/data stream is explicitly deferred.

## RabbitMQ wire contract

Every publish made by the shared client sets:

| AMQP value | Behavior |
|---|---|
| message_id | Required application identifier |
| correlation_id | Optional; current FindAir stage outputs omit it because traceparent provides correlation |
| content_type | application/json for application messages |
| delivery_mode | Persistent (2) |
| exchange and routing key | Configured destination |
| mandatory | true |
| body | Exact application payload bytes |

The application header allowlist is:

| Header | Behavior |
|---|---|
| traceparent | Current W3C producer context |
| tracestate | Current W3C trace state when present |
| findair-started-at-unix-ms | Created at first FindAir entry and preserved for end-to-end timing |
| findair-published-at-unix-ms | Replaced immediately before each publish |
| retry-count | Zero for normal output; incremented for delayed retries |
| algorithmName | Comma-separated validated values such as FindAir,Rpn |
| findair-contract-version | Minimal wire-metadata contract version, currently 1 |

### Embedder headers-exchange routing

`algorithmName` is a routing header, not unconstrained metadata. Its value is
canonicalized to exactly one of:

- `FindAir`
- `Rpn`
- `FindAir,Rpn`

RabbitMQ headers exchanges compare complete header values; they do not interpret
the comma-separated value as a list. To route the combined value to both
algorithm queues, configure these bindings on `embedder_Exchange.input`:

| Queue | x-match | algorithmName |
|---|---|---|
| FindAir queue | all | FindAir |
| FindAir queue | all | FindAir,Rpn |
| Rpn queue | all | Rpn |
| Rpn queue | all | FindAir,Rpn |

If one Embedder queue handles both algorithms, bind all three values to that same
queue instead. The routing key does not select queues for a headers exchange.
The Consumer publishes with `mandatory: true` and publisher-confirmation
tracking, so a message that matches no binding fails publication and the input
is not acknowledged as successfully processed.

Baggage and arbitrary incoming business headers are not forwarded. The shared
publisher filters headers case-insensitively before every publish, so callers
cannot bypass this contract accidentally. Message bodies, URLs, credentials,
WKT, coordinate arrays, and customer metadata are never copied into headers.

Gateway attaches algorithm names for the matched rule. TB Publisher and TB
Consumer refresh algorithmName from their validated payloads. Tile Builder
must preserve the received allowlisted headers unchanged. Retry routing keeps
the start time, increments the retry count, and refreshes trace and publish
time. RabbitMQ may add broker-owned x-death headers during dead-lettering.

Message ID remains useful independently of traceparent: it identifies one
delivery for retry, duplicate, and broker investigation. Trace ID correlates
the distributed execution. Correlation ID is not duplicated on current
inter-service outputs.

## ECS application logs

The existing stream naming is:

- logs-findair-production
- logs-findair-integration
- logs-findair-development

All remain in the same application dataset for now.

Every application log contains:

- @timestamp
- ecs.version
- message
- log.level and log.logger
- event.kind, event.code, optional event.action, and event.dataset
- data_stream.type, data_stream.dataset, and data_stream.namespace
- service.name, service.namespace, service.version, service.environment,
  service.instance.id, and optional service.node.name
- available kubernetes and orchestrator identity

Trace-correlated logs add trace.id and span.id. Exceptions add error.type,
error.message, and error.stack_trace.

The serializer deliberately does not repeat host, process, thread, language,
or runtime fields on every log document. Those values remain OpenTelemetry
resource metadata for traces and metrics.

When `areaOfInterest` is missing or blank, Gateway emits one Warning for the
image and continues. Logs and traces omit `findair.area.name`; workload metrics
use the bounded value `unknown`.

Known structured context is flattened to:

- messaging.message.id
- messaging.message.conversation_id when an input supplied one
- messaging.destination.name
- findair.retry.attempt
- findair.task.id
- findair.request.id
- findair.image.id
- findair.rule.id
- findair.tenant.id
- findair.algorithm.names
- findair.area.name
- findair.sensor.name
- findair.tile.id
- findair.tile.index

Known HTTP fields map to http.request.method, http.response.status_code,
http.route, url.path, and sanitized url.full. Query strings, fragments, and
credentials are removed. Other bounded structured values go under labels.
OriginalFormat and rendered scope object strings are not stored.

Important success events are:

| Event | Meaning | Level |
|---|---|---|
| 2010 | Gateway image processed and tenant tasks built | Information |
| 3015 | TB Publisher task processed and Tile Builder requests published | Information |
| 4015 | TB Consumer Tile Builder batch completed | Information |
| 4017 | One tile was published and broker-confirmed to Embedder | Information |
| 2021 | Routine successful rule-cache refresh | Debug, console only with checked-in filters |

Rejections and retries are Warning. Unrecoverable failures are Error. Normal
rule-refresh and Rules API operation detail remains Debug.

Event 4017 includes tile ID/index, sanitized tile URI, resolution, tile size,
and active task/request/image/rule/tenant/algorithm/area/sensor context. It is
intentionally one event per confirmed tile because that was selected as the
initial operational requirement.

At the expected tile volume this event is expensive. Trace sampling does not
reduce log volume. The Logstash exporter is bounded and non-blocking: during
sustained exporter pressure it drops records rather than reducing business
throughput. Alert on findair.logs.dropped. A durable guarantee for every
success log would require backpressure or a durable local queue and would
change the throughput/failure contract.

## Trace contract

Resource identity includes service name, namespace, version, instance,
deployment environment, pod/container identity, host, process, and .NET
runtime information when available.

The intended trace shape is:

    Gateway RabbitMQ consume (one trace per input image)
      parse
      rule match
      output build
      RabbitMQ producer per rule/tenant task
        TB Publisher consume
          projection stage
            Projection Mapper logical operation
            HttpClient dependency
          RabbitMQ producer per Tile Builder request
            Tile Builder preserves W3C headers
              TB Consumer consume for one Tile Builder batch
                projection stage
                  Projection Mapper logical operation
                  HttpClient dependency
                embedder.publish_batch aggregate producer

TB Consumer does not create validation/build spans or one native producer span
per tile. One aggregate embedder.publish_batch span represents the confirmed
batch publication. Rule-cache refresh creates metrics and logs, not spans.

Projection Mapper logical spans and HttpClient spans include HTTP method,
sanitized endpoint/path/route, server address/port, status, and dependency
metadata. URLs never include overlay query values, credentials, or signed
parameters.

Sampled application spans may contain task, request, image, rule, tenant, area,
sensor, algorithms, batch tile count, and tile ID where relevant. WKT,
coordinates, bodies, and signed URLs are excluded.

The default head sampler is parent-based 100 percent. Collector tail sampling
can later retain all error/slow traces plus a ratio of successful traces. Tail
sampling affects traces only, never application logs or Prometheus metrics.

## Completion and latency semantics

The start header is created on the first Gateway-side publish and survives all
stages. TB Consumer records findair.end_to_end.duration only after every tile in
the current Tile Builder message has been published and broker-confirmed.

That measurement is exact Gateway-to-completed-batch latency. It is not exact
final-task latency when Tile Builder divides one task across multiple messages
processed by different TB Consumer pods.

Exact final-task completion requires shared durable aggregation keyed by
task/request ID and tile indexes or a dedicated completion event from Tile
Builder. No pod-local counter is used because it would race in a multi-pod
service. Elasticsearch can compute an offline task view from task ID, total
tile count, tile index, tile ID, and confirmed-tile log timestamps.

## Prometheus metrics

OpenTelemetry dots are normally exposed as underscores. Counters receive a
total suffix and histograms expose bucket, sum, and count series.

### Workload

| Instrument | Labels |
|---|---|
| findair.images | outcome, area, sensor |
| findair.tasks | direction, outcome, rule, tenant, area, sensor, algorithms |
| findair.tile.requests | outcome, rule, tenant, area, sensor, algorithms |
| findair.tile.batches | outcome, rule, tenant, area, sensor, algorithms |
| findair.tiles | outcome, rule, tenant, area, sensor, algorithms, tile size |
| findair.tile.publish.attempts | outcome, rule, tenant, area, sensor, algorithms |
| findair.messages | stage, direction, outcome, optional bounded error |
| findair.payload.size | stage, direction |
| findair.stage.duration | stage, outcome, optional bounded error |
| findair.external_stage.duration | stage |
| findair.fanout | stage |
| findair.batch.size | stage, item |
| findair.end_to_end.duration | stage |

Tile publish is an attempt metric. Retries may count the same deterministic
logical tile more than once. Use tile.id logs for logical-tile deduplication.

### RabbitMQ and dependencies

- messaging.client.sent.messages and messaging.client.consumed.messages
- messaging.client.operation.duration and messaging.process.duration
- findair.messaging message-size, delivery-delay, inflight, settlement, and
  retry instruments
- findair.rabbitmq connection, channel, channel-wait, and restart instruments
- findair.dependency operation, duration, payload-size, and batch-size
  instruments

### Gateway and Rules API

Gateway exposes rule-cache entries, age, skipped rules, refresh outcomes and
duration, evaluated/matched rules, and photo-age filtering. Rules API exposes
operation outcomes/durations, document and batch counts, and validation
failures.

### Log exporter

- findair.logs.queue.size
- findair.logs.dropped, labeled by bounded drop reason
- findair.logs.export.requests
- findair.logs.export.duration

### Label policy

Prometheus labels may use bounded business dimensions: rule ID, tenant ID,
normalized area/country, sensor name, algorithm combination, configured tile
size, stage, direction, operation, outcome, dependency, and bounded error
category.

Prometheus labels must not use message, correlation, task, request, image,
overlay, tile, or trace IDs; raw URLs; exception text; stack traces; payloads;
WKT; coordinates; or pod identity emitted by application instruments. Pod
identity belongs to resource/target labels.

Standard .NET runtime, ASP.NET Core, Kestrel, and HttpClient metrics remain
enabled. Broker queue depth and unacked delivery metrics should come from the
RabbitMQ exporter. Container and pod resource metrics should come from
OpenShift/Kubernetes exporters.

## Checked-in logging filters

The checked-in service configuration enables JSON console logging and direct
Logstash logging together with different thresholds:

- console default: Debug
- EcsHttp/Logstash default: Information
- Microsoft.AspNetCore and noisy HttpClient categories: Warning
- Microsoft.Hosting.Lifetime: Information where configured

This keeps local Debug diagnostics visible without sending routine Debug
volume to Elasticsearch.

## Configuration summary

Required trace export values:

    OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
    OTEL_EXPORTER_OTLP_PROTOCOL=grpc

or the HTTP/protobuf equivalents. Logs use:

    Observability__Logs__Logstash__Endpoint=http://logstash:8080
    Observability__Logs__DataStream__Dataset=findair
    Observability__Logs__DataStream__Namespace=integration

Metrics use each pod's /metrics endpoint. Gateway, TB Publisher, and TB
Consumer default to port 9464. Rules API uses its existing HTTP port.

Service identity defaults to namespace findair and service names
findair-gateway, findair-rules-api, findair-tb-publisher, and
findair-tb-consumer. Deployment environment, service version, pod UID/name,
namespace, deployment, node, container, and cluster should be supplied by the
OpenShift config and downward API.
