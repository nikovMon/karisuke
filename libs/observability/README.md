# ImagingPipeline.Observability

Shared observability bootstrap and instrumentation contract for the Imaging Pipeline services.

The three signals intentionally use different transports:

| Signal | Application destination | Final destination |
| --- | --- | --- |
| Traces | OpenTelemetry Collector over OTLP | Elastic APM managed by Fleet, then Elasticsearch |
| Logs | Logstash HTTP input as bounded ECS JSON batches | Elasticsearch log data stream |
| Metrics | Prometheus scrape endpoint | Prometheus 2.36.1 |

Only traces use OTLP. This library does not register OTLP exporters for logs or metrics.

## Host registration

The Gateway, TB Publisher, and TB Consumer are workers with a lightweight ASP.NET Core listener used only for Prometheus:

```csharp
using ImagingPipeline.Observability;

var builder = WebApplication.CreateBuilder(args);
builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);
builder.ConfigureImagingPipelinePrometheusListener();

// Register the worker and application services.

var app = builder.Build();
app.MapImagingPipelinePrometheusScrapingEndpoint();
await app.RunAsync();
```

By default, the worker listener binds all interfaces on port `9464` and serves `/metrics`.

Rules API already has an HTTP listener, so it maps metrics on that listener instead of opening another port:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.AddImagingPipelineObservability(
    ObservabilityServiceNames.RulesApi,
    instrumentAspNetCore: true);

var app = builder.Build();
app.MapImagingPipelinePrometheusScrapingEndpoint();
await app.RunAsync();
```

Do not call `ConfigureImagingPipelinePrometheusListener` in Rules API. Its configured Prometheus path is served on the API port.

## Application configuration

A complete configuration has the following shape:

```json
{
  "Observability": {
    "Enabled": true,
    "ServiceNamespace": "findair",
    "ServiceVersion": "1.2.3",
    "DeploymentEnvironment": "production",
    "Traces": {
      "Enabled": true,
      "OtlpEnabled": true,
      "RecordExceptions": true,
      "ExcludeHealthChecks": true,
      "DefaultSamplingRatio": 1.0
    },
    "Metrics": {
      "Enabled": true,
      "Prometheus": {
        "Enabled": true,
        "Path": "/metrics",
        "Port": 9464
      }
    },
    "Logs": {
      "Enabled": true,
      "ConsoleEnabled": true,
      "Logstash": {
        "Enabled": true,
        "Endpoint": "http://logstash.example:8080",
        "QueueCapacity": 10000,
        "PriorityQueueCapacity": 1000,
        "WarningQueueCapacity": 1000,
        "BatchSize": 100,
        "FlushIntervalMilliseconds": 1000,
        "RequestTimeoutSeconds": 5,
        "MaxRetryAttempts": 3,
        "RetryBaseDelayMilliseconds": 200,
        "ShutdownFlushTimeoutSeconds": 5,
        "MaxAttributeCount": 64,
        "MaxCollectionCount": 32,
        "MaxStringLength": 8192
      },
      "DataStream": {
        "Dataset": "findair",
        "Namespace": "production"
      }
    }
  }
}
```

Environment variables use the normal .NET double-underscore mapping. Examples:

```text
Observability__ServiceVersion=1.2.3
Observability__DeploymentEnvironment=production
Observability__Traces__OtlpEnabled=true
Observability__Metrics__Prometheus__Port=9464
Observability__Metrics__Prometheus__Path=/metrics
Observability__Logs__Logstash__Endpoint=http://logstash:8080
Observability__Logs__DataStream__Dataset=findair
Observability__Logs__DataStream__Namespace=production
```

`OTEL_SDK_DISABLED=true` disables collection while W3C RabbitMQ context propagation remains registered.

## Traces: OTLP Collector

Trace export requires a standard OTLP endpoint and protocol when both tracing and trace OTLP export are enabled. Startup fails with an explicit configuration error if either is absent or invalid.

gRPC example:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

HTTP/protobuf example:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

The signal-specific alternatives take precedence:

```text
OTEL_EXPORTER_OTLP_TRACES_ENDPOINT=http://otel-collector:4318/v1/traces
OTEL_EXPORTER_OTLP_TRACES_PROTOCOL=http/protobuf
```

Supported protocols are `grpc` and `http/protobuf`. The endpoint must be an absolute HTTP or HTTPS URL and must not contain credentials. Standard OTLP exporter variables still control exporter behavior. Store any future secret headers in an OpenShift Secret, never in appsettings.

No connection to the Collector is attempted during configuration validation. A temporary Collector outage must not prevent business processing; the OpenTelemetry exporter handles delivery asynchronously.

### Sampling

When `OTEL_TRACES_SAMPLER` is absent, the library uses a parent-based ratio sampler with `Observability:Traces:DefaultSamplingRatio`. The default ratio is `1.0`, so every new root trace is sampled. A valid unsampled upstream parent remains unsampled, which avoids creating misleading partial traces.

Standard sampler variables override the application fallback:

```text
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=1.0
```

Tail sampling belongs in the OpenTelemetry Collector. Tail sampling decides whether to retain a trace after the Collector has observed its spans. If the Collector must choose all slow or failed traces, applications need to send all candidate traces to it; head-sampling them away in the application cannot be repaired later.

The main trace begins when Gateway consumes its RabbitMQ input and continues through Gateway, TB Publisher, the external Tile Builder hop, and TB Consumer. All tile messages produced for one task keep the same trace ID and use distinct producer/consumer spans. Tile Builder must preserve the incoming trace headers, and it must export to the same Elastic APM destination for its internal spans to appear in the same Kibana waterfall.

## Logs: direct ECS HTTP to Logstash

Logs do not go to the OpenTelemetry Collector. The `ILogger` provider formats ECS-compatible JSON and enqueues it into an in-process bounded buffer. A hosted exporter sends batches directly to the configured Logstash HTTP input.

This hosted exporter is a runtime dependency, but it is isolated from business processing:

- Startup validates the endpoint and buffer settings but does not contact Logstash.
- Logging threads enqueue records without waiting for an HTTP request.
- The exporter batches records and retries timeouts, network errors, HTTP `408`, `429`, and `5xx` responses with bounded exponential backoff and jitter.
- Other non-success `4xx` responses are not retried.
- When the normal queue is full, lower-priority records are dropped instead of blocking message processing.
- `Error` and `Critical` records use a reserved priority queue and fall back to normal capacity when available.
- If both queues are full, even a priority record is dropped and a rate-limited emergency message is written directly to stderr.
- Shutdown performs a bounded flush.

This is deliberate: an unavailable logging system must not stop RabbitMQ consumption or exhaust pod memory. Monitor the log exporter metrics described below and alert on drops.

If Logstash HTTP logging is enabled, `Observability:Logs:Logstash:Endpoint` is required and must be an absolute HTTP or HTTPS URL without embedded credentials. `Dataset` and `Namespace` must contain only lowercase letters, digits, underscores, or dots. The log dataset defaults to `findair`. Set the namespace explicitly to `production`, `integration`, or `development` in each deployment configuration; when omitted, it falls back to the normalized deployment environment. Keeping these settings separate lets the final `logs-findair-{namespace}` data-stream name change without a code change.

Each document includes the ECS data-stream, service identity, environment,
event, logger, Kubernetes, trace-correlation, and error fields described in
docs/observability-contract.md. Repeated process/thread/runtime fields are not
written on every log document; they remain OpenTelemetry resource attributes.

TelemetryLogContext flattens message/destination/retry plus task, request,
image, rule, tenant, algorithms, area, sensor, tile ID, and tile index into
canonical messaging and findair fields. Known HTTP values become sanitized
http, url, and server fields. OriginalFormat and rendered scope object strings
are removed.

The HTTP exporter is intentionally non-blocking and bounded. One Information
event is currently emitted for every broker-confirmed tile. At high volume,
monitor findair.logs.dropped; trace sampling does not reduce application log
volume.
## Metrics: Prometheus scraping

Metrics are pulled by Prometheus; the applications do not push them to the Collector, Elastic APM, Logstash, or Elasticsearch.

| Service | Listener | Default path |
| --- | --- | --- |
| Gateway | Dedicated worker listener on `9464` | `/metrics` |
| TB Publisher | Dedicated worker listener on `9464` | `/metrics` |
| TB Consumer | Dedicated worker listener on `9464` | `/metrics` |
| Rules API | Existing API listener | `/metrics` |

Every pod may use the same container port because pods have separate network namespaces. Prometheus must discover and scrape every pod. The endpoint has no application authentication; restrict access with OpenShift networking and Prometheus discovery configuration.

`Prometheus:Port` must be from `1` through `65535`. `Prometheus:Path` must be an absolute path such as `/metrics`; query strings, fragments, route parameters, wildcards, and whitespace are rejected. The scrape route is excluded from ASP.NET Core trace and request-metric instrumentation.

OpenTelemetry metric names are translated into Prometheus names. Dots become underscores, counters normally receive a `_total` suffix, and histograms expose `_bucket`, `_sum`, and `_count` series. Resource information is exposed separately, commonly as `target_info`.

The strongly typed recorder groups are:

- MessagingTelemetry for RabbitMQ transport, processing, settlement, retry,
  payload, delay, connection, channel, and pool-wait data.
- DependencyTelemetry for Elasticsearch, Projection Mapper, RabbitMQ, and HTTP
  dependency duration, outcome, payload, and batch data.
- PipelineTelemetry for stage traffic, duration, external transit, batch
  completion latency, fan-out, and batch sizes.
- WorkloadTelemetry for images, tasks, Tile Builder requests/batches, logical
  tile counts, and Embedder publish attempts, grouped only by bounded business
  dimensions.
- GatewayTelemetry for cache and rule-matching data.
- RulesTelemetry for CRUD/bulk outcomes, duration, counts, and validation.
Log-export health is observable through:

| OpenTelemetry instrument | Type | Bounded labels |
| --- | --- | --- |
| `findair.logs.queue.size` | Gauge | none |
| `findair.logs.dropped` | Counter | `findair.logs.drop.reason` |
| `findair.logs.export.requests` | Counter | `findair.outcome`, optional `http.response.status_code_class` |
| `findair.logs.export.duration` | Histogram | the same bounded outcome/status-class labels |

Drop reasons are fixed implementation values such as `queue_full`, `priority_queue_full`, `formatting`, `serialization`, `transport`, and `shutdown`; they are not exception messages.

Metric labels may contain bounded dimensions such as stage, direction,
operation, outcome, error category, dependency, rule ID, tenant ID, normalized
area/country, sensor name, algorithm combination, and configured tile size.

Metric labels must never contain message, correlation, task, request, image,
overlay, tile, or trace IDs; raw URLs; exception text; stack traces; payloads;
WKT; coordinates; or application-emitted pod identity. Pod identity belongs to
resource and Prometheus target labels.
Prometheus handles application and runtime metrics. Broker queue depth, unacked deliveries, RabbitMQ cluster memory, container CPU/memory/network, and pod restarts should come from their authoritative RabbitMQ and OpenShift exporters rather than being independently emitted by every application pod.

## Resource identity

The trace resource and ECS log documents share the same core service identity. Configure:

```text
OTEL_SERVICE_NAME
SERVICE_VERSION
DEPLOYMENT_ENVIRONMENT
POD_NAME
POD_UID
POD_NAMESPACE
DEPLOYMENT_NAME
NODE_NAME
CONTAINER_NAME
CLUSTER_NAME
```

`Observability:ServiceVersion` takes precedence over `SERVICE_VERSION`. `Observability:DeploymentEnvironment` takes precedence over `DEPLOYMENT_ENVIRONMENT`, then `DOTNET_ENVIRONMENT` or `ASPNETCORE_ENVIRONMENT`. `service.instance.id` uses `POD_UID`, then `POD_NAME`, then a process-unique fallback.

The resulting resource includes service name/namespace/version/instance, deployment environment, host and .NET runtime information, and available `k8s.*` attributes. The Collector may add further cluster metadata with its Kubernetes attributes processor.

## RabbitMQ trace propagation

RabbitMQ.Client publisher and subscriber activity sources are registered
centrally. Callers do not add duplicate manual transport spans.

IMessageTraceContextPropagator injects and extracts only W3C traceparent and
tracestate. Baggage is removed. The shared RabbitMqPublisher applies a strict
case-insensitive header allowlist containing trace context, FindAir start and
per-hop timestamps, algorithmName, findair-contract-version, and the
configured retry-count header. Arbitrary business headers are dropped.

Tile Builder must forward these headers unchanged. IDs needed for business
processing remain in the JSON contract; high-cardinality IDs belong in
logs/traces and never Prometheus labels.
## Instrumentation and performance rules

- Sources, meters, and instruments are static for process lifetime.
- Keep traces aggregate; do not create one span per rule, coordinate, or tile.
- Check `Activity.IsAllDataRequested` before computing expensive optional span data.
- Use source-generated `[LoggerMessage]` methods and structured fields.
- Use metrics for aggregate accounting; the current user-selected confirmed-tile Information event is the explicit high-volume exception.
- Never serialize a payload only to calculate a telemetry size.
- Never place message bodies, URLs, credentials, WKT, coordinate arrays, or unconstrained customer metadata in logs, baggage, span attributes, or metric labels.
- Keep Logstash queues bounded. Increasing pod resources does not make an unbounded telemetry queue safe.
- Alert on dropped logs, Logstash export failures, Collector export failures, and missing Prometheus scrape targets.

Package versions are managed centrally. Keep the OpenTelemetry API, SDK, instrumentation, OTLP trace exporter, and Prometheus exporter on the repository's aligned version line.
