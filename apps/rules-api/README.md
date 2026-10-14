# Rules API

`rules-api` manages the v2 rules of each pipeline in the catalog. It exposes REST routes under `/pipelines/{pipelineId}/rules`, stores each pipeline's rules in that pipeline's Elasticsearch index, and returns JSON.

Swagger is available at `/swagger` when the app is running. Health is available at `/health`.

## Runtime

Default local settings:

```json
{
  "Elasticsearch": {
    "Uri": "http://localhost:9200",
    "AuditIndex": "rules-audit",
    "TimeoutSeconds": 30,
    "Username": "",
    "Password": "",
    "DefaultSearchSize": 100,
    "MaxSearchSize": 1000,
    "MaxBulkSize": 500
  }
}
```

Set both credentials when Elasticsearch authentication is enabled. Prefer environment variables instead of storing secrets in the file:

```bash
Elasticsearch__Username=elastic
Elasticsearch__Password=your-secret
```

The client sends HTTP Basic Authentication whenever both values are configured. Supplying only one credential fails configuration validation during startup.

Run locally:

```bash
dotnet run --project apps/rules-api/ImagingPipeline.Rules.Api.csproj
```

Docker Compose exposes the service on host port `8080`.

### Pipeline catalog

The API loads the same pipeline catalog as the unified gateway: `pipelinecatalog.json` next to the app, or the file named by the `PipelineCatalogFile` setting. An invalid catalog stops startup. The API reads pipeline IDs and contracts from it, never transport settings. The catalog still has to validate in full, so RabbitMQ credentials only need placeholder values here.

## Rule Schema

Rules are `PipelineRuleDocument`s (schema version 2) in `libs/common-dtos`: `ruleName`, `description`, `isActive`, `matchAll`, `match` (`sensors`, `resolution`, `locationWkt`, `maxPhotoAgeDays`) and `runParams` in the shape the pipeline's contract defines. The routes below describe how they are created and changed.

## Response Shapes

Error response:

```json
{
  "error": "Rule 'rule-001' was not found."
}
```

Bulk operation response:

```json
{
  "successIds": ["rule-001"],
  "failedIds": [
    {
      "id": "missing-rule",
      "reason": "Rule not found"
    }
  ]
}
```

Repository failures, usually Elasticsearch failures, return `503 Service Unavailable`.

## Observability

The API exports traces to the OpenTelemetry Collector over OTLP, exposes metrics at `/metrics` for Prometheus, and sends correlated ECS JSON logs to Logstash over HTTP.

- ASP.NET Core produces the HTTP server spans and request-duration metrics; a separate request-logging middleware is intentionally not used.
- Rules actions add child spans and bounded metrics for operation outcome, duration, validation failures, requested bulk size, and returned or changed document counts.
- Elasticsearch client operations add dependency spans and metrics for get, search, index, and delete calls.
- Rule and document IDs are attached only to spans and logs, never to metric labels.
- Health-check success traffic is excluded from traces and does not produce success logs; failures still emit warnings.
- Elasticsearch failures include exception details in server telemetry, while clients receive only a stable generic error.
- Request bodies, credentials, and Elasticsearch debug payloads are not logged.

Debug logs are disabled by default. Enable them temporarily for the Rules API category:

```bash
Logging__LogLevel__ImagingPipeline.Rules.Api=Debug
```

Trace and span identifiers are supplied by OpenTelemetry log correlation rather than repeated in message templates.

## Routes

### GET /pipelines

Lists every configured pipeline, enabled or not, ordered by ID. Transport settings are never returned.

```json
[
  { "pipelineId": "DeepWisdom", "contractId": "DeepWisdom", "enabled": true },
  { "pipelineId": "FindAir", "contractId": "FindAir", "enabled": true }
]
```

### GET /pipelines/{pipelineId}

Returns one pipeline in the same shape, or `404 Not Found` when the ID is not in the catalog.

### GET /pipelines/{pipelineId}/rules

Lists the pipeline's v2 rules (`PipelineRuleDocument`) from the index the catalog names for it. Query parameters: `getNameOnly` (return names only), `isActive`, `from` and `size` (default `DefaultSearchSize`, at most `MaxSearchSize`).

### GET /pipelines/{pipelineId}/rules/{id}

### GET /pipelines/{pipelineId}/rules/name/{ruleName}

Return one v2 rule, or `404 Not Found`.

### POST /pipelines/{pipelineId}/rules

Creates a v2 rule from `ruleName`, `description`, `isActive` (default `true`), `matchAll`, `match` and `runParams`. The server sets the ID, `schemaVersion` and timestamps, and derives `match.location` from `match.locationWkt`. The rule is checked with the same validator the gateway uses when it loads rules: the document rules, then the pipeline contract's run parameters and location requirement. Returns `201 Created`, `400 Bad Request` with the validation errors, or `409 Conflict` when the name is already used in this pipeline. Creates, deletes and renames wait for the index refresh, so the next request's name check sees them. An update that changes nothing is not saved.

### PATCH /pipelines/{pipelineId}/rules/{id}

Partial update. Only the fields sent change; a field sent as `null` is cleared. Fields inside `match` are patched one by one, so `{ "match": { "maxPhotoAgeDays": 7 } }` keeps the other conditions, and `{ "match": null }` removes them all. The updated rule must pass the same validation as on create.

### PATCH /pipelines/{pipelineId}/rules/{id}/activity

Body `{ "isActive": true|false }`. Activation is validated like any other update. Deactivation is not, so a rule that stopped passing validation, for example after a contract change, can still be switched off.

### PATCH /pipelines/{pipelineId}/rules/bulk?ids=a,b

Applies one partial update, in the same shape as `PATCH /pipelines/{pipelineId}/rules/{id}`, to each listed rule. Each rule is validated and saved on its own. Returns `200 OK` when all succeed, `207 Multi-Status` when some fail and `422 Unprocessable Entity` when all fail, with `successIds` and `failedIds` (each with a reason). Setting `ruleName` on more than one rule returns `409 Conflict`. Duplicate IDs are applied once, and more than `Elasticsearch:MaxBulkSize` IDs (default 500) returns `400 Bad Request`. A rule that cannot be saved is listed in `failedIds` while the others are still saved.

### PATCH /pipelines/{pipelineId}/rules/sensors/add?ids=a,b

### PATCH /pipelines/{pipelineId}/rules/sensors/remove?ids=a,b

Body `{ "sensorName": "...", "registrationQualities": [...], "gridTypes": [...] }`, the same as the v1 routes. Add merges the values into the sensor in `match.sensors`, creating it when missing. Remove takes the values out and drops a sensor left with none. Results are reported per rule, as in bulk update. Two cases fail for that rule instead of changing what it matches:

- Removing a rule's last sensor, which would make it match every sensor. To do that on purpose, send `match.sensors` as `null` in a PATCH.
- Adding a sensor to a `matchAll` rule, which cannot have conditions.

Adding a sensor to a rule with no sensor condition limits it to that sensor.

### DELETE /pipelines/{pipelineId}/rules/{id}

Deletes the rule permanently. Returns `204 No Content` or `404 Not Found`.

Rule writes log events 5031–5037 with `pipeline.id`, `findair.rule.id` and, where relevant, `unified_gateway.rule.changed_fields` or `unified_gateway.rule.validation.errors` as fields. Bulk requests skip per-rule not-found warnings and log one summary (5038) with the operation, requested, succeeded and failed counts, and up to 10 failed IDs.

Every `/pipelines/{pipelineId}/...` route answers `404 Not Found` for a pipeline that is not in the catalog, without querying Elasticsearch, and logs event 5030 with `pipeline.id` as a field.

### GET /health

Checks whether the API process can connect to Elasticsearch.

Response `200 OK`:

```json
{
  "status": "Healthy"
}
```

When Elasticsearch is unavailable, the endpoint returns `503 Service Unavailable`:

```json
{
  "status": "Unhealthy"
}
```

## Rule Audit

Every v2 rule change appends one record to `Elasticsearch:AuditIndex` (default `rules-audit`), shared by all pipelines:

```json
{
  "pipelineId": "FindAir",
  "ruleId": "6f1c...",
  "changeType": "created | updated | activated | deactivated | deleted",
  "previousState": { },
  "newState": { },
  "changedFields": ["match.sensors"],
  "changedBy": null,
  "changedAt": "2026-10-12T10:00:00+00:00",
  "reason": null,
  "requestId": "<trace id>"
}
```

- The states are full rule snapshots. A creation has no `previousState` and a deletion has no `newState`.
- `changedFields` and `changeType` come from comparing the stored rule before and after, not from what the request sent. `activated` and `deactivated` are used when `isActive` is the only change; any other change is `updated`. An update that changes nothing writes no record.
- `requestId` is the HTTP request's `TraceIdentifier`.
- `changedBy` and `reason` stay null until the API authenticates its callers.
- Records are written after the rule is saved. A failed write logs warning 5039 and does not fail the request.

Different pipelines' `runParams` can give the same field different types, so the audit index should store the states without indexing them:

```json
{ "mappings": { "properties": {
  "previousState": { "type": "object", "enabled": false },
  "newState": { "type": "object", "enabled": false }
} } }
```

## Elasticsearch Behavior

Each pipeline's rules live in the index the catalog names for it (`RulesIndex`). The rule ID is the Elasticsearch metadata `_id`; it is added to API responses but excluded from `_source`. Duplicate name checks use the exact `ruleName.keyword` value within the pipeline's index.

## Test Coverage

Routes, validation, audit and logging are covered by `tests/rules-api-tests`.

Run:

```bash
dotnet test tests/rules-api-tests/ImagingPipeline.Rules.Api.Tests.csproj
```
