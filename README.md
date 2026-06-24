# Karisuke

Karisuke is a minimal C#/.NET 10 monorepo for an imaging pipeline base. It provides the project structure, build orchestration, tests, and Docker packaging needed to start implementation later.

Nx is the monorepo task orchestrator. `dotnet` and MSBuild perform the actual restore, build, test, run, and publish work.

## Prerequisites

- .NET SDK 10.0.300.
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
tests/
  gateway-tests/
  tb-publisher-tests/
  tb-consumer-tests/
  rules-api-tests/
  integration-tests/
```

`libs` is intentionally empty except for `.gitkeep`. Future shared libraries can be added under `libs/` when a real shared boundary exists. Do not move DTOs into shared libraries until there is a concrete contract to share.

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
```

Run tests through Nx:

```powershell
npx nx test gateway-tests
npx nx test tb-publisher-tests
npx nx test tb-consumer-tests
npx nx test rules-api-tests
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

To add a future shared library, create it under `libs/<name>`, add it to the solution, and reference it only from projects that truly need the shared boundary.

To add a new test project, create it under `tests/<name>-tests`, add it to the solution, reference only the application or library under test, and keep it non-packable.
