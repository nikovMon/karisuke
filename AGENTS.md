# AGENTS.md

Instructions for AI coding agents (and a fast orientation for humans) working in **karisuke**: a C# / .NET 10 imaging-pipeline monorepo orchestrated by Nx. Keep this file short; deep detail lives in `docs/` and in each project's README. If this file and the code disagree, the code wins, then fix this file in the same MR.

## Stack

- .NET SDK `10.0.300` (`global.json`), C# 14, `net10.0`. Nullable enabled, `TreatWarningsAsErrors=true`, `AnalysisLevel=10.0` (`Directory.Build.props`). A new warning is a build failure.
- NuGet versions are centrally managed in `Directory.Packages.props`. Never put a `Version=` on a `PackageReference`.
- Nx 23 (`@nx/dotnet`) is the task runner; `dotnet`/MSBuild does the real work. Solution file: `ImagingPipeline.sln`.
- Infra the services talk to: RabbitMQ, Elasticsearch, Projection Mapper (HTTP), OpenTelemetry Collector, Logstash, Prometheus. There is **no Redis** in this repo.
- Files use **CRLF** line endings and 4-space indent for `.cs` (`.editorconfig`). Preserve them; do not reformat whole files.

## Commands (run from repo root)

```bash
npm install                                  # once
npx nx build <project>                       # e.g. gateway, unified-gateway, rules-api, rule-engine
npx nx test <project>-tests                  # e.g. rule-engine-tests
npx nx affected -t build test                # what you must run before declaring done
npx nx graph --file=docs/project-graph.json  # dependency graph (optional)
dotnet format ImagingPipeline.sln --verify-no-changes
```

`integration-tests` may need local infra (`compose.yaml`); run it only when your change touches cross-service behavior, and say so in the MR.

## Layout

```
apps/   deployable services: gateway (legacy FindAir), unified-gateway (new), rules-api, tb-publisher, tb-consumer
libs/   shared libraries: common-dtos, pipeline-catalog, pipeline-contracts, rule-engine, dispatch,
        rabbitmq-client, rabbitmq-configuration, elasticsearch-client, projection-mapper-client,
        geometry-utils, observability
tests/  one <name>-tests project per app/lib, plus integration-tests
tools/  dev tooling (dispatch-harness)
deploy/ example/fragment manifests (observability, unified-gateway). NOT the authoritative production config
docs/   specs and reference docs (observability contract, telemetry pattern)
```

Each app/lib has (or should have) a README with purpose, config and commands: read it before editing that project.

## Conventions

- **Adding a project:** app under `apps/<name>`, lib under `libs/<name>`, tests under `tests/<name>-tests` (non-packable, reference only what they test). Add every new project to `ImagingPipeline.sln`. DTOs stay local to an app unless used by several apps (then `libs/common-dtos`). Add `project.json` only for custom targets (e.g. `docker-build`).
- **Dependencies point inward:** apps may use libs; libs must not reference apps; do not create cross-app project references.
- **Logging:** each app owns a `<App>Log.cs` for its log messages; follow that pattern, use structured fields, never string-concatenate into messages.
- **Telemetry:** use the shared primitives from `libs/observability` (`PipelineStageScope`, `PipelineSpanScope`, `WorkloadDimensions`). Metric labels must be bounded-cardinality. Rules and examples: `docs/pipeline-telemetry-pattern.md`, `docs/observability-contract.md`.
- **Messaging:** preserve inbound headers (incl. W3C trace context, `retry-count`) when republishing; routing contracts are in `docs/observability-contract.md` (RabbitMQ wire contract). Every dispatch must carry an idempotency key.
- **Tests:** put a test in the matching `tests/<name>-tests` project for every behavior change; bug fixes start with a failing test. Never delete, skip or weaken a test to make a build pass. If a test is wrong, say why in the MR.
- **Commits:** Conventional Commits with the project as scope, e.g. `feat(rules-api): ...`, `fix(rabbitmq-client): ...`. One logical change per commit. Branches: `feature/<area>/<topic>`, `fix/<topic>`, `refactor/<topic>`; target `main` via MR.

## Never (without explicit human approval in the task)

- Touch `deploy/**`, `Dockerfile`s, CI config, `global.json`, or `Directory.*.props`.
- Add or upgrade a package, or add a project reference between apps.
- Change a wire contract (RabbitMQ headers/body, HTTP envelope, rule schema, metric names or labels) or `libs/pipeline-contracts` / `libs/common-dtos` public types.
- Commit secrets, real hostnames, real credentials or real imagery/customer data. Use placeholders and `.example` files.
- Change the high-scale pipeline's behavior. Agents may propose, a human implements.
- Run destructive commands (`rm -rf` outside build output, `git push --force`, history rewrites).

## When to stop and ask a human

- The task is ambiguous or contradicts a doc or the code.
- You need something on the "Never" list.
- The same fix has failed 3 times.
- The change is growing well past what was asked (roughly 300 lines).

## Definition of done

- [ ] Every stated requirement is covered by a test
- [ ] `npx nx affected -t build test` passes; no new warnings; `dotnet format --verify-no-changes` clean. Show the real output.
- [ ] New projects are in `ImagingPipeline.sln`
- [ ] Logs/metrics follow the telemetry docs; no unbounded label values
- [ ] README / SPEC / reference docs the change touches are updated in the same change; no doc now contradicts the code
- [ ] You state what you were unsure about

## Docs map

| Need | Read |
|---|---|
| Contract/behavior of a component | `docs/` (SPEC files), project README |
| Logs, traces, metrics, wire headers | `docs/observability-contract.md`, `docs/pipeline-telemetry-pattern.md` |
