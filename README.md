# Karisuke

Karisuke is a C#/.NET 10 imaging-pipeline monorepo. It contains the Rules API, Gateway, Tile Builder publisher/consumer boundaries, shared data and dependency clients, OpenTelemetry instrumentation, tests, and Docker packaging.

Nx is the monorepo task orchestrator. `dotnet` and MSBuild perform the actual restore, build, test, run, and publish work.

## Prerequisites

- .NET SDK 10.0.301.
- Node.js 24.18.0 or newer 24.x LTS.
- npm 11 or newer.
- Docker, when container builds or Compose are needed.

## Structure

```text
apps/
  gateway/
  tb-publisher/
  tb-consumer/
  rules-api/
libs/
  common-dtos/
  elasticsearch-client/
  geometry-utils/
  observability/
  projection-mapper-client/
  rabbitmq-client/
tests/
  elasticsearch-client-tests/
  gateway-tests/
  observability-tests/
  projection-mapper-client-tests/
  rabbitmq-client-tests/
  tb-publisher-tests/
  tb-consumer-tests/
  rules-api-tests/
  integration-tests/
```

`libs/common-dtos` contains DTO contracts shared by multiple apps. `libs/observability` is the central OpenTelemetry contract and host bootstrap. The RabbitMQ, Projection Mapper, and Elasticsearch libraries own their dependency instrumentation while using that common contract.

Tile Builder and Embedder implementations are not present in this repository; their current integration boundary is represented by RabbitMQ DTOs and the publisher/consumer applications.

The external Tile Builder forwards W3C trace/baggage and timing headers but exports
its own telemetry to a separate backend. TB Consumer therefore continues the same
trace identity while this repository reports the otherwise invisible boundary as a
bounded `tile_builder` external-stage transit metric; Tile Builder spans themselves
remain visible only in its telemetry backend.

## Observability

All runnable applications use the same correlation contract, but each signal follows its own production path:

- traces use OTLP to an OpenTelemetry Collector and then Elastic APM;
- ECS structured logs use HTTP to Logstash and then Elasticsearch data streams;
- application and .NET runtime metrics are exposed on `/metrics` for Prometheus scraping.

Configure the trace exporter with standard OpenTelemetry environment variables:

```text
OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=1.0
```

RabbitMQ carries W3C trace context, trace state, bounded allowlisted baggage, and a stable pipeline-origin timestamp across services. The applications emit aggregate stage spans and bounded-cardinality latency, throughput, payload-size, fan-out, batch, dependency, runtime, and messaging metrics. Broker queue depth and OpenShift container/node metrics should be scraped once from their Prometheus-compatible exporters rather than emitted by every application pod.

The checked-in fallback samples 100% of new root traces with parent-based decisions when
`OTEL_TRACES_SAMPLER` is absent. Production still needs the Collector endpoint and an
explicit, capacity-tested sampler in the external OpenShift deployment configuration;
the checked-in fragments are examples rather than complete deployment manifests.

See [deploy/observability/README.md](deploy/observability/README.md) for the complete Collector, Logstash, Prometheus, Elasticsearch data-stream, and OpenShift configuration, and [libs/observability/README.md](libs/observability/README.md) for the runtime contract.

## Install

```powershell
npm install
```

## .NET Commands

```powershell
dotnet restore ImagingPipeline.sln
dotnet build ImagingPipeline.sln --configuration Release
dotnet test ImagingPipeline.sln --configuration Release
```

## Nx Commands

```powershell
npm run restore
npm run build
npm run test
npm run affected:build
npm run affected:test
npm run nx:graph
```

Build individual projects with Nx:

```powershell
npx nx build gateway
npx nx build tb-publisher
npx nx build tb-consumer
npx nx build rules-api
npx nx build common-dtos
npx nx build observability
npx nx build rabbitmq-client
```

Run tests through Nx:

```powershell
npx nx test gateway-tests
npx nx test tb-publisher-tests
npx nx test tb-consumer-tests
npx nx test rules-api-tests
npx nx test observability-tests
npx nx test integration-tests
```

## Docker

Build all application images through Nx:

```powershell
npm run docker:build
```

Build one image directly:

```powershell
docker build -f apps/rules-api/Dockerfile -t karisuke/rules-api:local .
```

Start the four application containers:

```powershell
docker compose up --build
```

The `rules-api` service is exposed on host port `8080`.

## Adding Projects

To add a new application, create a project under `apps/<name>`, add it to `ImagingPipeline.sln`, keep DTOs local under that application, and add a `project.json` only when custom targets such as Docker builds are required.

To add another shared library, create it under `libs/<name>`, add it to the solution, and reference it only from projects that truly need the shared boundary.

To add a new test project, create it under `tests/<name>-tests`, add it to the solution, reference only the application or library under test, and keep it non-packable.
