# Observability deployment configuration

This directory contains copyable configuration fragments for the FindAir
observability architecture. Production, integration, and local development all
write to one shared Elasticsearch/Kibana deployment:

```text
Production applications -> production Logstash :8081 -> logs-findair-production
Integration applications -> integration Logstash :8081 -> logs-findair-integration
Local development        -> integration Logstash :8082 -> logs-findair-development
                                                       -> shared Elasticsearch 8.15
```

The production and integration Logstash deployments already exist and are also
used by other systems. FindAir adds isolated pipelines to them; it does not
replace their existing pipelines. These fragments do not deploy Logstash, an
OpenTelemetry Collector, or Prometheus, and do not apply Elasticsearch changes
automatically.

## Files

- `logstash/pipelines.production.yml.example` registers the production FindAir
  pipeline; `logstash/pipelines.integration.yml.example` registers the separate
  integration and development pipelines.
- `logstash/findair-production.conf`, `logstash/findair-integration.conf`, and
  `logstash/findair-development.conf` provide the three fixed-destination
  pipelines.
- `logstash/production.openshift.fragment.yaml` and
  `logstash/integration.openshift.fragment.yaml` show the ports, mounts,
  secrets, and persistent storage to merge into the existing deployments.
- `prometheus/scrape-config.fragment.yaml` discovers annotated application pods
  and scrapes their metrics endpoints.
- `otel-collector/traces-receiver.fragment.yaml` defines only the
  application-facing OTLP trace receiver and processors.
- `openshift/application-observability.fragment.yaml` shows the application
  environment, Downward API fields, Prometheus annotations, and worker metrics
  port.
- `elasticsearch/kibana-dev-tools.http` creates and verifies the ECS log data
  stream template and its seven-day lifecycle.

## Version scope

The examples deliberately target:

- Logstash 8.5.3, including its Elasticsearch output plugin's ECS v8 and data
  stream options.
- Elasticsearch 8.15.0 data-stream lifecycle.
- Prometheus 2.36.1 Kubernetes pod discovery and pull scraping.

Logstash 8.5.3 is much older than Elasticsearch 8.15.0. The data-stream
features used here are available in Logstash 8.5.3, but aligning Logstash with
the Elasticsearch minor line is still recommended as a later operational
upgrade.

The Collector version and its Fleet-managed APM exporter are owned outside
this repository. The Collector file is intentionally not a complete,
standalone configuration.

## Setup order

1. Create and verify the shared Elasticsearch data-stream template in Kibana
   Dev Tools.
2. Add the FindAir production pipeline and port `8081` to production Logstash.
3. Configure and roll out production applications, then validate production.
4. Later, add the isolated integration (`8081`) and development (`8082`)
   pipelines to integration Logstash.
5. Merge the OTLP receiver into the existing Collector and add the Prometheus
   scrape job.
6. Configure integration and development, then perform the remaining checks.

Creating the Elasticsearch template first prevents a first log event from
creating a stream with unintended dynamic mappings or lifecycle settings.

## Elasticsearch and Kibana

### Data-stream names

The dataset is fixed to `findair`. Each environment has a separate stream in
the same Elasticsearch cluster:

```text
logs-findair-production
logs-findair-integration
logs-findair-development
```

It follows the Elastic naming convention:

```text
logs-<dataset>-<namespace>
```

The application metadata and its destination pipeline must stay aligned:

| Source | Dataset | Namespace |
| --- | --- | --- |
| Production application and production Logstash `:8081` | `findair` | `production` |
| Integration application and integration Logstash `:8081` | `findair` | `integration` |
| Local application and integration Logstash `:8082` | `findair` | `development` |

Logstash fixes the actual destination in each pipeline. Application fields are
metadata and cannot select another environment's stream. This prevents a local
configuration mistake from mixing development logs with integration or
production logs.

Run `elasticsearch/kibana-dev-tools.http` in Kibana **Dev Tools > Console**.
The file:

1. verifies the Elasticsearch 8.15 built-in `logs@mappings`,
   `logs@settings`, and `ecs@mappings` components;
2. creates a seven-day data-stream lifecycle component;
3. creates a specific `logs-findair-*` data-stream template;
4. simulates template resolution and creates production first;
5. includes the later integration and development stream requests;
6. verifies the effective lifecycle for all three streams.

The lifecycle guarantees that data is retained for at least seven days.
Deletion is asynchronous and happens after rollover/lifecycle processing, so
it is not an exact delete-at-seven-days SLA.

### Kibana data view

After the data stream exists:

1. Open **Stack Management > Data Views**.
2. Select **Create data view**.
3. Set the name to `FindAir Logs`.
4. Set the index pattern to `logs-findair-*`.
5. Select `@timestamp` as the time field.

The standard `logs-*-*` Observability source normally includes this stream. If
the Kibana deployment uses a restricted custom logs source, add
`logs-findair-*` to it. Log documents contain `trace.id` and
`span.id`, which enables direct log-to-trace navigation when a sampled activity
is active.

Do not dynamically select a namespace in one Logstash output. If another
environment is added, give it a dedicated input/pipeline with a fixed namespace.
The existing `logs-findair-*` template covers it. Elasticsearch data streams
cannot be renamed; create a replacement stream, switch writes, and allow the
old stream to expire under its lifecycle.

## Logstash: existing deployments, isolated FindAir pipelines

There are two existing Logstash deployments. Both keep their pipelines for
other systems unchanged and connect to the same shared Elasticsearch cluster.
Add FindAir as follows:

| Logstash deployment | Pipeline | Input | Fixed destination |
| --- | --- | --- | --- |
| Production | `findair-production` | `0.0.0.0:8081` | `logs-findair-production` |
| Integration | `findair-integration` | `0.0.0.0:8081` | `logs-findair-integration` |
| Integration | `findair-development` | `0.0.0.0:8082` | `logs-findair-development` |

Configure only the production row initially. Add the two integration Logstash
pipelines later. Development deliberately uses its own port and pipeline, so
it is never mixed into the integration stream.

Use the files under `logstash/` as fragments for each existing deployment's
ConfigMap and `/usr/share/logstash/config/pipelines.yml`. Keep the existing
pipeline IDs and tuning unchanged. Each FindAir output must hard-code dataset
`findair` and its row's namespace; do not route from application-supplied
`data_stream.*` fields. Reuse the existing Elasticsearch TLS endpoint and
credentials mechanism in both Logstash deployments.

The HTTP input receives canonical ECS JSON. Request headers and the remote host
are redirected under `@metadata`, so Logstash cannot replace application fields
such as `url.path`, `http.*`, or `host.*`.

Logstash must start through its normal entrypoint without `-f` or `-e`.
Logstash ignores `pipelines.yml` when either command-line option is present.

Each FindAir HTTP input:

- listens on the port assigned in the table above;
- requires `Content-Type: application/json`;
- returns HTTP `202` after accepting the request;
- accepts either one ECS JSON object or a root JSON array;
- expands a root array into one event per log record;
- stores ingress request headers under
  `[@metadata][input][http][request][headers]` and the remote host under
  `[@metadata][input][http][remote_host]`, which are not indexed;
- does not require TLS, authentication, CORS, or custom headers.

The Elasticsearch output still uses TLS and authentication. Keep secrets in an
OpenShift Secret or Logstash keystore, not in a ConfigMap. Dataset, namespace,
port, and pipeline ID should remain explicit in the pipeline configuration.
The output disables Logstash template and ILM management because the shared
Elasticsearch 8.15 template and seven-day lifecycle are created through Kibana.

### Persistent queue and failure behavior

Each FindAir pipeline uses its own 1 GiB persistent queue. Mount
`/usr/share/logstash/data` on durable storage if accepted events must survive a
pod replacement. An ephemeral volume protects only against temporary
Elasticsearch backpressure while that pod remains alive.

Pipelines in one deployment have separate queues, so a blocked output in one
does not directly backpressure another. They still share that deployment's JVM,
CPU, memory, pod, and process failure domain. Size `pipeline.workers` and the
JVM heap for all pipelines in each deployment together.

The application logger is asynchronous and bounded. If Logstash remains
unavailable long enough to fill its in-memory queues, lower-priority logs may
be dropped so logging cannot stop RabbitMQ processing. A Logstash HTTP `202`
means Logstash accepted the batch; it does not mean Elasticsearch has already
indexed it.

Keep ports `8081` and `8082` private. Restrict them with OpenShift
NetworkPolicy. Local development should reach integration Logstash `:8082`
through the approved VPN or `oc port-forward`; do not publish an unauthenticated
HTTP input as a public Route.

## OpenTelemetry Collector and trace sampling

Merge `otel-collector/traces-receiver.fragment.yaml` into the existing
Collector. It exposes:

- OTLP/gRPC on `4317`;
- OTLP/HTTP protobuf on `4318`;
- a memory limiter before batching;
- one trace pipeline.

Replace `YOUR_EXISTING_FLEET_APM_EXPORTER` with the exporter already configured
for Fleet-managed Elastic APM. This repository intentionally does not define
the Collector-to-APM endpoint, TLS, or credentials.

There is no metrics or logs pipeline in this Collector fragment:

- logs go directly to Logstash over HTTP;
- Prometheus pulls metrics from the applications.

Applications default to 100% head sampling using
`parentbased_always_on`. A remote parent marked as unsampled remains
unsampled, which preserves distributed-tracing semantics. The Gateway normally
starts the pipeline trace, so its root is sampled.

Do not add Collector tail sampling initially. Tail sampling buffers traces and
requires every span for a trace to reach the same sampling decision point. It
also conflicts with the requirement to retain every pipeline task trace.

For OTLP/gRPC:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
```

For OTLP/HTTP:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4318
OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
```

With the base `OTEL_EXPORTER_OTLP_ENDPOINT`, do not append `/v1/traces`; the
.NET exporter applies the signal path for HTTP/protobuf.

## Prometheus

Metrics are not sent to the Collector, APM, Logstash, or Elasticsearch.
Prometheus is the metrics system of record and scrapes every application pod.

Merge `prometheus/scrape-config.fragment.yaml` into the existing Prometheus
2.36.1 configuration and replace `YOUR_OPENSHIFT_PROJECT`. Its service account
must have `get`, `list`, and `watch` access to pods in that project. Network
policies must allow it to reach application pod IPs.

Per-service endpoints:

| Service | `service.name` | Metrics endpoint |
| --- | --- | --- |
| Gateway | `imaging-pipeline-gateway` | `http://<pod-ip>:9464/metrics` |
| TB Publisher | `imaging-pipeline-tb-publisher` | `http://<pod-ip>:9464/metrics` |
| TB Consumer | `imaging-pipeline-tb-consumer` | `http://<pod-ip>:9464/metrics` |
| Rules API | `imaging-pipeline-rules-api` | `http://<pod-ip>:8080/metrics` |

The scrape job adds only bounded resource labels:

- `service_name`
- `service_version`
- `service_namespace`
- `deployment_environment`
- `k8s_namespace_name`
- `k8s_pod_name`
- `k8s_pod_uid`
- `k8s_node_name`

Never add tenant, task, request, message, correlation, image, rule, or sensor
identifiers as metric labels. Those values are unbounded and would damage
Prometheus cardinality and query performance.

Prometheus scraping is pull-based. A Service is not required because the
scrape job discovers pod IPs, but container ports, pod annotations, RBAC, and
NetworkPolicy must permit access.

Metrics stored only in Prometheus do not appear in Kibana APM and do not carry
direct `trace.id` links. Correlate them with traces and logs by service,
environment, pod, and time.

## Application environment

Prefer application settings in the JSON configuration mounted from an
OpenShift ConfigMap. Environment variables are optional overrides for those
settings. Runtime identity such as pod metadata and secrets still belongs in
environment variables or Secret references. Use
`openshift/application-observability.fragment.yaml` as the deployment starting
point.

### Common settings

| Variable | Required value or example | Purpose |
| --- | --- | --- |
| `Observability__Enabled` | `true` | Master observability switch |
| `Observability__ServiceNamespace` | `imaging-pipeline` | Shared service namespace |
| `Observability__DeploymentEnvironment` | `production` | Environment shown in Elastic |
| `OTEL_SERVICE_NAME` | service-specific name | Explicit service identity |
| `SERVICE_VERSION` | image version or Git SHA | Service version in traces and logs |

Do not ship a production image with `SERVICE_VERSION=unknown`. Set it from CI
using the immutable image version or commit SHA.

### Traces

| Variable | Value |
| --- | --- |
| `Observability__Traces__Enabled` | `true` |
| `Observability__Traces__OtlpEnabled` | `true` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | `http://otel-collector:4317` |
| `OTEL_EXPORTER_OTLP_PROTOCOL` | `grpc` |
| `OTEL_TRACES_SAMPLER` | `parentbased_always_on` |

Missing or malformed required trace configuration must fail application
startup with a configuration error. Startup validation checks syntax only; it
must not contact the Collector. A temporary Collector outage must not stop the
business service.

### Metrics

| Variable | Workers | Rules API |
| --- | --- | --- |
| `Observability__Metrics__Enabled` | `true` | `true` |
| `Observability__Metrics__Prometheus__Enabled` | `true` | `true` |
| `Observability__Metrics__Prometheus__Port` | `9464` | `8080` |
| `Observability__Metrics__Prometheus__Path` | `/metrics` | `/metrics` |
| `OTEL_METRICS_EXPORTER` | `none` | `none` |

Workers host a small Kestrel endpoint on port 9464. Rules API maps `/metrics`
on its existing port 8080. The `/metrics` route must be excluded from tracing
to avoid one trace for every Prometheus scrape.

### Logs

Configure production first. A minimal production JSON configuration is:

```json
{
  "Observability": {
    "DeploymentEnvironment": "production",
    "Logs": {
      "Enabled": true,
      "ConsoleEnabled": false,
      "Logstash": {
        "Enabled": true,
        "Endpoint": "http://production-logstash:8081/"
      },
      "DataStream": {
        "Dataset": "findair",
        "Namespace": "production"
      }
    }
  }
}
```

Replace the service name with the actual OpenShift Service. Later, integration
uses integration Logstash `:8081` and namespace `integration`; local development
uses reachable integration Logstash `:8082` and namespace `development`.
Logstash controls the actual destination for each port.

The equivalent environment-variable keys below are optional overrides, not a
requirement to move these values out of JSON:

| Variable | Recommended value | Purpose |
| --- | --- | --- |
| `Observability__Logs__Enabled` | `true` | Enables structured application logging |
| `Observability__Logs__ConsoleEnabled` | `false` | Avoids duplicating full logs on stdout |
| `Observability__Logs__Logstash__Enabled` | `true` | Enables direct HTTP export |
| `Observability__Logs__Logstash__Endpoint` | `http://production-logstash:8081/` | Production Logstash input; override per environment |
| `Observability__Logs__Logstash__QueueCapacity` | `10000` | Total bounded in-memory capacity |
| `Observability__Logs__Logstash__PriorityQueueCapacity` | `1000` | Capacity reserved for error/critical records |
| `Observability__Logs__Logstash__BatchSize` | `100` | Maximum records per HTTP batch |
| `Observability__Logs__Logstash__FlushIntervalMilliseconds` | `1000` | Maximum partial-batch wait |
| `Observability__Logs__Logstash__RequestTimeoutSeconds` | `5` | Per-request timeout |
| `Observability__Logs__Logstash__MaxRetryAttempts` | `3` | Bounded 429/5xx retries |
| `Observability__Logs__Logstash__RetryBaseDelayMilliseconds` | `200` | Initial retry backoff |
| `Observability__Logs__Logstash__ShutdownFlushTimeoutSeconds` | `5` | Bounded shutdown drain |
| `Observability__Logs__Logstash__MaxAttributeCount` | `64` | Maximum structured attributes retained per log record |
| `Observability__Logs__Logstash__MaxCollectionCount` | `32` | Maximum items retained from one structured collection |
| `Observability__Logs__Logstash__MaxStringLength` | `8192` | Maximum retained characters per string value |
| `Observability__Logs__DataStream__Dataset` | `findair` | ECS data-stream dataset |
| `Observability__Logs__DataStream__Namespace` | `production` | ECS data-stream namespace |
| `OTEL_LOGS_EXPORTER` | `none` | Explicitly prevents OTLP log export |

The application endpoint has no TLS, authentication, or custom headers. It
sends `Content-Type: application/json`.

Missing or malformed required logging configuration must fail startup.
Startup does not probe Logstash. Once running, logging uses bounded queues,
bounded retries with backoff, a dropped-log metric, and rate-limited emergency
stderr reporting. A Logstash outage must not stop RabbitMQ consumption or
publishing.

### OpenShift resource metadata

Every application receives:

| Variable | Source |
| --- | --- |
| `POD_NAME` | `metadata.name` |
| `POD_UID` | `metadata.uid` |
| `POD_NAMESPACE` | `metadata.namespace` |
| `NODE_NAME` | `spec.nodeName` |
| `CONTAINER_NAME` | Explicit value per Deployment |
| `DEPLOYMENT_NAME` | Explicit value per Deployment |
| `SERVICE_VERSION` | CI/image version |
| `DEPLOYMENT_ENVIRONMENT` | ConfigMap or explicit value |
| `CLUSTER_NAME` | ConfigMap or explicit cluster identity |

The Downward API cannot reliably derive the owning Deployment or container
name, so those two values are explicit. Pod UID is used as the stable
`service.instance.id` for one pod lifetime.

## OpenShift rollout details

For each worker:

1. merge the common environment and Downward API fields;
2. expose container port 9464;
3. add the Prometheus scrape annotations;
4. allow Prometheus ingress to port 9464.

For Rules API:

1. merge the same environment and Downward API fields;
2. set the Prometheus annotation port to `8080`;
3. reuse its existing HTTP container port;
4. allow Prometheus ingress to port 8080.

Pod labels `app.kubernetes.io/part-of=imaging-pipeline`,
`app.kubernetes.io/name`, `app.kubernetes.io/version`, and `environment`
are required by the supplied Prometheus relabeling rules. The `part-of` keep
rule prevents this job from also scraping unrelated annotated pods in the same
OpenShift project.

## Validation

### Elasticsearch

Before application rollout, run the simulation and lifecycle checks from
`elasticsearch/kibana-dev-tools.http`. Confirm:

- the specific `findair_logs` template wins;
- `logs@mappings`, `logs@settings`, and `ecs@mappings` are composed;
- data-stream lifecycle is enabled;
- effective retention is `7d`.

### Logstash

Validate production first with the exact Logstash 8.5.3 image and its existing
Elasticsearch connection settings:

```sh
/usr/share/logstash/bin/logstash \
  --path.settings /usr/share/logstash/config \
  --config.test_and_exit \
  -f /usr/share/logstash/pipeline/findair-production.conf
```

Before the later integration rollout, repeat the isolated check for
`findair-integration.conf` and `findair-development.conf`.

The `-f` option is appropriate only for this isolated syntax check. Do not use
it in the real container entrypoint.

After each rollout, confirm the expected FindAir pipeline IDs appear in
Logstash startup logs and that every pre-existing pipeline still receives
traffic.

Send a test batch from a pod in an allowed namespace:

```sh
curl -i \
  -H 'Content-Type: application/json' \
  --data '[{"@timestamp":"2026-07-30T10:00:00Z","ecs":{"version":"8.0.0"},"message":"observability ingestion test","log":{"level":"information","logger":"deployment-test"},"service":{"name":"imaging-pipeline-gateway","namespace":"imaging-pipeline","version":"test","environment":"production"},"event":{"dataset":"findair"}}]' \
  http://production-logstash:8081/
```

Expect HTTP `202`, then find the document in the
`logs-findair-production` data stream. Remove or exclude the test
record from operational dashboards if necessary.

### Prometheus

After merging the fragment into the complete Prometheus file:

```sh
promtool check config /etc/prometheus/prometheus.yml
```

From an authorized pod:

```sh
curl -fsS http://GATEWAY_POD_IP:9464/metrics
curl -fsS http://RULES_API_POD_IP:8080/metrics
```

In Prometheus, open **Status > Targets** and verify one healthy target per
application pod. Query:

```promql
up{job="imaging-pipeline"}
```

### Collector

Merge the receiver fragment into the complete Collector configuration, replace
the exporter placeholder, and run the validation command supported by that
Collector distribution. The fragment cannot validate alone because it
intentionally references an externally owned Fleet APM exporter.

Confirm both OTLP ports listen and send a real pipeline message. In Kibana APM,
verify that Gateway, TB Publisher, and TB Consumer spans share one trace ID.
The external Tile Builder must preserve `traceparent`, `tracestate`, and
`baggage` unchanged.

### Application startup validation

In a non-production environment, remove one required endpoint or set an
invalid port/path and verify that startup fails with the exact configuration
key in the error. Then restore valid syntax but stop the remote Collector or
Logstash and verify that the application remains alive and continues business
processing.

## Limitations

- The Collector-to-Fleet-APM exporter remains externally owned.
- Metrics live only in Prometheus and have no direct Kibana trace exemplar
  links.
- The OpenTelemetry .NET Prometheus scrape exporter is prerelease and does not
  support exemplars. Kestrel is used for workers instead of the development-only
  standalone HttpListener exporter.
- Pipelines within each Logstash deployment share that deployment's JVM and
  failure domain; production and integration Logstash remain separate.
- A bounded in-process log exporter can drop records during a sufficiently
  long Logstash outage. It must never block the business hot path.
- A Logstash persistent queue is durable across pod replacement only when
  `/usr/share/logstash/data` uses durable storage.
- Seven-day data-stream retention is a minimum retention period, not an exact
  deletion deadline.
- No application-to-Collector or application-to-Logstash TLS/authentication is
  configured by requirement. Enforce cluster-local reachability with
  NetworkPolicy.
- The Tile Builder preserves trace headers but its own internal spans appear in
  the Kibana waterfall only if it exports them to the same tracing backend.
