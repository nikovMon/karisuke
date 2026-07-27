# ImagingPipeline.Observability

Central OpenTelemetry bootstrap and instrumentation contract for Imaging Pipeline services. It exports traces, metrics, and `ILogger` records through OTLP to an OpenTelemetry Collector. It has no Elastic-specific exporter; the Collector owns the Elastic APM/Fleet connection.

## Host registration

Reference this project and register it before other services:

```csharp
using ImagingPipeline.Observability;

var builder = Host.CreateApplicationBuilder(args);
builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);
```

For ASP.NET Core, enable server instrumentation:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddImagingPipelineObservability(
    ObservabilityServiceNames.RulesApi,
    instrumentAspNetCore: true);
```

The registration includes:

- OTLP batch export for traces and logs, and periodic OTLP export for metrics.
- .NET runtime and `HttpClient` instrumentation.
- Optional ASP.NET Core traces and metrics.
- RabbitMQ.Client 7 publisher/subscriber activity sources.
- W3C trace IDs, automatic trace/log correlation, structured scopes, and optional JSON console logs.
- Explicit latency, payload-size, and count histogram buckets.
- OpenShift resource identity.

## Configuration

Application settings control signal enablement only. Exporter, sampler, processor, TLS, and authentication settings use standard `OTEL_*` environment variables.

```json
{
  "Observability": {
    "Enabled": true,
    "Otlp": {
      "Enabled": true
    },
    "ServiceNamespace": "imaging-pipeline",
    "ServiceVersion": "1.2.3",
    "DeploymentEnvironment": "production",
    "Traces": {
      "Enabled": true,
      "RecordExceptions": true,
      "ExcludeHealthChecks": true,
      "DefaultSamplingRatio": 0.10
    },
    "Metrics": {
      "Enabled": true
    },
    "Logs": {
      "Enabled": true,
      "OtlpEnabled": true,
      "ConsoleEnabled": false,
      "IncludeFormattedMessage": true,
      "IncludeScopes": true,
      "ParseStateValues": true
    }
  }
}
```

Recommended OpenShift environment:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_SERVICE_NAME=imaging-pipeline-gateway
OTEL_RESOURCE_ATTRIBUTES=service.namespace=imaging-pipeline,deployment.environment.name=production
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=0.10
OTEL_METRIC_EXPORT_INTERVAL=10000
```

When `OTEL_TRACES_SAMPLER` is absent, the common library installs a safe
`parentbased_traceidratio` fallback using `Traces:DefaultSamplingRatio` (10% in the
checked-in settings). Standard sampler environment variables override that fallback.
Set the endpoint and sampler explicitly in the real OpenShift Deployment/Helm/Kustomize
configuration; this repository does not contain those manifests. Start with a measured
ratio per service and raise it only when pod/exporter/Collector headroom is known. If
the Collector performs tail sampling, it can only choose among spans the applications
actually sent.

The checked-in application defaults send logs directly over OTLP and disable JSON
console output, so OpenShift does not serialize or ingest every record twice. If your
cluster collects container stdout instead, set `Logs:OtlpEnabled=false` and
`Logs:ConsoleEnabled=true`.

Size the standard batch-processor queues only after measuring pod memory and peak
telemetry volume. The relevant controls are `OTEL_BSP_MAX_QUEUE_SIZE`,
`OTEL_BSP_MAX_EXPORT_BATCH_SIZE`, and `OTEL_BSP_SCHEDULE_DELAY` for traces, plus the
corresponding `OTEL_BLRP_*` variables for logs. Alert on Collector refused/dropped
items before increasing application queues.

Use an OpenShift Secret for `OTEL_EXPORTER_OTLP_HEADERS`. Never store exporter credentials in appsettings.

The resource detector consumes these Downward API variables when available:

```text
POD_NAME POD_UID POD_NAMESPACE NODE_NAME CONTAINER_NAME DEPLOYMENT_NAME
SERVICE_VERSION DEPLOYMENT_ENVIRONMENT
```

`service.instance.id` uses `POD_UID`, then `POD_NAME`, then a process-unique fallback. Let the Collector's `k8sattributes` processor enrich additional Kubernetes/OpenShift metadata.

## RabbitMQ propagation

RabbitMQ.Client 7.2.1 emits native publisher and subscriber activities. They are registered here so callers must not add duplicate manual transport spans.

`IMessageTraceContextPropagator` is available for explicit transport integration, compatibility tests, or a client path that does not propagate automatically. It injects and extracts W3C `traceparent`, `tracestate`, and `baggage`, accepting Rabbit header values represented as `string`, `byte[]`, `Memory<byte>`, or `ReadOnlyMemory<byte>`.

Inject only after the producer activity starts. Extract before starting application processing. A malformed header must be treated as missing context and must never reject a business message.

The propagator enforces a small allowlist (`tenant` plus the central pipeline task,
request, image, rule, tenant, and algorithm keys), eight entries, 128-byte keys,
256-byte values, and a 2 KiB total header. Oversized or unknown baggage is dropped
without dropping valid trace context. Never place bodies, URLs, credentials, WKT,
coordinate arrays, or unconstrained customer metadata in baggage; every downstream
hop pays its serialization and network cost.

`PipelineCorrelationBaggage.Push` creates a short-lived outbound scope containing
only the canonical task, request, image, rule, tenant, and algorithm keys. Validated
message-body values overwrite inbound baggage, values above 256 UTF-8 bytes are
omitted, and the previous ambient baggage is restored after success, failure, or
cancellation. After validating a message, TB Publisher and TB Consumer use this
scope for their Projection Mapper HTTP calls and downstream RabbitMQ publishes. TB
Consumer refreshes every value, including the Tile Builder-generated request ID,
from the validated output body. IDs remain trace/log correlation data and never
become metric dimensions.

## Traces and logs

Use the component activity sources in `TelemetrySources`. Keep trace shapes aggregate: do not create a span for every rule, coordinate, or tile. Counts belong on a containing span and in metrics.

`SetTelemetryError` records bounded error category, exception event, and error status. `AddPipelineContext` adds high-cardinality task/request/image/rule/tenant identifiers only to sampled spans. `BeginTelemetryScope` provides the same operational context to structured logs; OTLP log records carry native trace and span IDs without duplicating them as scope attributes. JSON-console mode adds those two identifiers explicitly.

Do not manually include trace IDs in log templates. Prefer source-generated `[LoggerMessage]` methods. Normal per-message success belongs at `Debug`; lifecycle summaries belong at `Information`; retries/rejections at `Warning`; exhausted or unrecoverable failures at `Error`.

Never log or trace message bodies, WKT, coordinate arrays, raw Elasticsearch queries, credentials, arbitrary headers, or signed image URLs.

Enable either `Logs:OtlpEnabled` or `Logs:ConsoleEnabled` for production—not both—or records will be duplicated. The separate switches let traces and metrics continue using OTLP when logs are collected from stdout. Set `Observability:Otlp:Enabled` to `false` for tests or local runs that intentionally have no Collector.

## Metrics and cardinality

Recorder APIs are strongly typed around bounded stage, operation, outcome, error, direction, and item enums. Durations are seconds and sizes are bytes.

Available recorders:

- `MessagingTelemetry`: send/consume/process/settle latency and throughput, payload size, delivery delay, retries, in-flight work, channels, connections, restarts, and channel-pool wait.
- `DependencyTelemetry`: Elasticsearch, Projection Mapper, RabbitMQ, and HTTP logical operations, latency, known payload sizes, and batch sizes. Clients never buffer or reserialize a body solely to obtain a telemetry size; a size is omitted when the transport exposes no content length.
- `PipelineTelemetry`: stage ingress/egress, outcomes, application-stage latency,
  external-stage transit latency, end-to-end latency, fan-out, and batch sizes.
- `GatewayTelemetry`: rule-cache entries/age/refresh and rule matching volumes.
- `RulesTelemetry`: logical operations, duration, documents, bulk sizes, and validation failures.

Metric attributes may contain only:

- Configured queue and exchange names.
- Bounded operation, stage, outcome, error, direction, and item values.
- HTTP route templates/status codes and fixed dependency/index names.

Metric attributes must never contain:

- Message, correlation, task, request, image, overlay, rule, or tenant IDs.
- Sensor names, raw paths/URLs, exception messages, stack traces, bodies, WKT, coordinates, or pod identity.

Pod identity is a resource attribute, not a metric label. Sum counters and in-flight values across pods for service throughput. Inspect cache gauges per pod or with max/average rather than sum. Broker queue depth, unacked messages, cluster memory, container CPU/memory/network, and pod restarts must be collected once by Collector RabbitMQ/kubelet receivers rather than emitted independently by every pod.

For true end-to-end latency, preserve an origin timestamp across every stage—including external Tile Builder and Embedder—and record `PipelineTelemetry.RecordEndToEndDuration` at the terminal stage. Pipeline-origin and per-hop publish timestamps older than 24 hours, more than five seconds in the future, or malformed are rejected so external headers cannot poison histogram sums; rejections increment `imaging_pipeline.telemetry.invalid_timing_headers`. The timing helpers accept an optional future-skew override; accepted negative elapsed time is clamped to zero.

Tile Builder currently exports its own spans and logs to a different telemetry
backend and forwards the incoming RabbitMQ headers unchanged. This preserves W3C
trace identity into TB Consumer, but Tile Builder spans are not visible in this
Collector/Elastic deployment and therefore appear as a gap in its trace timeline.
`imaging_pipeline.pipeline.external_stage.duration` with
`imaging_pipeline.pipeline.stage=tile_builder` measures that black-box transit from
TB Publisher's instrumented publish to the first TB Consumer delivery, including the
queues around Tile Builder. TB Consumer retries are excluded from that measurement
and use the normal refreshed RabbitMQ delivery clock.

Gateway fan-out and egress payload-size measurements describe outputs generated by the
handler. Because its shared RabbitMQ outcome router publishes only after the handler
returns, confirmed sends and publish latency come from `messaging.client.sent.messages`,
`messaging.client.operation.duration`, and `messaging.process.duration`. A generated
Gateway output is not counted as a successful pipeline egress before broker confirmation.

## Performance rules

- Sources, meters, and instruments are static for process lifetime.
- OTLP exporters are asynchronous/batched; never use a simple exporter in production.
- Avoid string interpolation and payload serialization for telemetry.
- Check `Activity.IsAllDataRequested` before computing expensive optional span data.
- Use metrics rather than success logs for full-rate throughput accounting.
- Use `parentbased_traceidratio` when application/export cost must be bounded. Use `always_on` only when the Collector performs tail sampling and retaining every slow/error trace justifies the extra network load.
- Monitor Collector refused/dropped telemetry, queue fill, export errors, and memory limiter activity.

Package versions are managed centrally. This library expects the OpenTelemetry packages to be kept on one stable version line.
