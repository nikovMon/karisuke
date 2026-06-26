# Rules API

`rules-api` manages rule configuration documents for the imaging pipeline. It exposes REST routes under `/rules`, stores documents in Elasticsearch, and returns JSON.

Swagger is available at `/swagger` when the app is running. Health is available at `/health`.

## Runtime

Default local settings:

```json
{
  "Elasticsearch": {
    "Uri": "http://localhost:9200",
    "DefaultIndex": "rules"
  },
  "Rules": {
    "IndexName": "rules"
  }
}
```

Run locally:

```bash
dotnet run --project apps/rules-api/ImagingPipeline.Rules.Api.csproj
```

Docker Compose exposes the service on host port `8080`.

## Rule Schema

Rule documents use this JSON shape:

```json
{
  "_id": "rule-001",
  "ruleName": "find-cars-tel-aviv",
  "description": "Find vehicles in Tel Aviv",
  "algorithmName": "Finder",
  "sensors": {
    "camera": ["rgb-main", "rgb-backup"],
    "satellite": ["sentinel-2"]
  },
  "isActive": true,
  "tenants": [
    {
      "tenantName": "tenant-a",
      "tilingConfig": {
        "width": 512,
        "length": 512
      }
    }
  ],
  "minResolution": 0.5,
  "maxResolution": 2.5,
  "area": "tel-aviv",
  "wkt": "POINT (1 1)",
  "geoJson": {
    "type": "Point",
    "coordinates": [1, 1]
  },
  "maxLookBackDay": 7,
  "createdAt": "2026-06-26T10:00:00Z",
  "modifiedAt": "2026-06-26T10:00:00Z"
}
```

Required validation:

- `_id` cannot be empty.
- `ruleName` cannot be empty and must be unique.
- `algorithmName` is required. Valid values are `Finder` and `rpn`.
- `minResolution` must be greater than `0`.
- At least one geometry field must be provided: `wkt`, `geoJson`, or both.

Notes:

- The JSON field names for resolution are `minResolution` and `maxResolution`.
- `createdAt` and `modifiedAt` are set by the service when creating a rule.
- `modifiedAt` changes whenever a rule is updated or sensor values are mutated.
- Sensor keys with empty names are removed during normalization.
- Empty sensor values and duplicate sensor values are removed during normalization.

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

## Routes

### GET /health

Checks whether the API process is running.

Response `200 OK`:

```json
{
  "status": "Healthy"
}
```

### GET /rules

Returns rule documents.

Query parameters:

- `getNameOnly`: optional boolean. Defaults to `false`.
- `isActive`: optional boolean. When set, filters active or inactive rules.

Examples:

```bash
curl "http://localhost:8080/rules"
curl "http://localhost:8080/rules?isActive=true"
curl "http://localhost:8080/rules?getNameOnly=true&isActive=true"
```

Response when `getNameOnly=false`:

```json
[
  {
    "_id": "rule-001",
    "ruleName": "find-cars-tel-aviv",
    "algorithmName": "Finder",
    "sensors": {},
    "isActive": true,
    "tenants": [],
    "minResolution": 0.5,
    "maxResolution": 2.5,
    "area": "tel-aviv",
    "wkt": "POINT (1 1)",
    "createdAt": "2026-06-26T10:00:00Z",
    "modifiedAt": "2026-06-26T10:00:00Z"
  }
]
```

Response when `getNameOnly=true`:

```json
["find-cars-tel-aviv"]
```

Status codes:

- `200 OK`
- `400 Bad Request` for invalid boolean query values.
- `503 Service Unavailable` when Elasticsearch access fails.

### GET /rules/{id}

Returns one rule by `_id`. Route matching is case-sensitive for ids.

Example:

```bash
curl "http://localhost:8080/rules/rule-001"
```

Status codes:

- `200 OK` with a rule document.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### GET /rules/name/{ruleName}

Returns one rule by `ruleName`. Name matching is case-sensitive.

Example:

```bash
curl "http://localhost:8080/rules/name/find-cars-tel-aviv"
```

Status codes:

- `200 OK` with a rule document.
- `404 Not Found` when the name does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### POST /rules

Creates a new rule.

Request body: full rule document.

Example:

```bash
curl -X POST "http://localhost:8080/rules" \
  -H "Content-Type: application/json" \
  -d '{
    "_id": "rule-001",
    "ruleName": "find-cars-tel-aviv",
    "description": "Find vehicles in Tel Aviv",
    "algorithmName": "Finder",
    "sensors": {
      "camera": ["rgb-main", "rgb-main", "rgb-backup"]
    },
    "isActive": true,
    "tenants": [],
    "minResolution": 0.5,
    "maxResolution": 2.5,
    "area": "tel-aviv",
    "wkt": "POINT (1 1)",
    "maxLookBackDay": 7
  }'
```

On success, `createdAt` and `modifiedAt` are set to the current UTC time. Duplicate sensor values are normalized.

Status codes:

- `201 Created` with the saved rule.
- `400 Bad Request` for invalid JSON, missing body, invalid enum, or validation errors.
- `409 Conflict` when `ruleName` already exists.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/{id}

Partially updates one rule. Only known fields sent in the body are changed. Unknown fields are ignored; if the request contains only unknown fields, the API rejects it as an empty update.

Request body: partial rule update.

```json
{
  "description": "Updated description",
  "isActive": false,
  "maxLookBackDay": 14
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/rule-001" \
  -H "Content-Type: application/json" \
  -d '{
    "description": "Updated description",
    "isActive": false
  }'
```

Patch rules:

- Missing fields remain unchanged.
- Explicit `null` clears nullable fields such as `description`, `wkt`, `geoJson`, and `maxLookBackDay`.
- `sensors` replaces the entire sensors object when sent through this route.
- `tenants` replaces the entire tenants array when sent.
- `ruleName` can be changed only when the new value is unique.
- `algorithmName` cannot be set to `null`.
- `minResolution` cannot be set to `null`, `0`, or a negative value.
- After the patch is applied, the full rule must still pass normal rule validation.

Status codes:

- `200 OK` with the updated rule.
- `400 Bad Request` for empty update, unknown-only update, malformed JSON, invalid enum, invalid field values, or invalid final rule.
- `404 Not Found` when the id does not exist.
- `409 Conflict` when changing `ruleName` to a name used by another rule.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/bulk

Applies the same partial update to multiple rules.

Query parameters:

- `ids`: comma-separated rule ids. Whitespace is trimmed and empty segments are ignored.

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/bulk?ids=rule-001,rule-002,missing-rule" \
  -H "Content-Type: application/json" \
  -d '{
    "isActive": false,
    "maxLookBackDay": 30
  }'
```

Response:

```json
{
  "successIds": ["rule-001", "rule-002"],
  "failedIds": [
    {
      "id": "missing-rule",
      "reason": "Rule 'missing-rule' was not found."
    }
  ]
}
```

Bulk rules:

- Existing rules are updated independently.
- Missing rules are reported in `failedIds`.
- Setting `ruleName` is allowed only when the request has exactly one id.
- Setting the same `ruleName` on multiple ids is rejected with `409 Conflict`.

Status codes:

- `200 OK` with `BulkOperationResult`.
- `400 Bad Request` for missing ids, empty update, malformed JSON, or invalid field values.
- `409 Conflict` when trying to set one `ruleName` on multiple rules.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/{id}/activity

Changes only the `isActive` flag for one rule.

Request body:

```json
{
  "isActive": true
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/rule-001/activity" \
  -H "Content-Type: application/json" \
  -d '{ "isActive": true }'
```

Status codes:

- `200 OK` with the updated rule.
- `400 Bad Request` for malformed JSON or missing body.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/sensors/add

Adds sensor values to one or more rules without replacing the whole `sensors` object.

Query parameters:

- `ids`: comma-separated rule ids.

Request body:

```json
{
  "sensorName": "camera",
  "values": ["rgb-side", "rgb-main"]
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/sensors/add?ids=rule-001,missing-rule" \
  -H "Content-Type: application/json" \
  -d '{
    "sensorName": "camera",
    "values": ["rgb-side", "rgb-main"]
  }'
```

Behavior:

- If the sensor key does not exist, it is created.
- Existing values are not duplicated.
- Missing rule ids are reported in `failedIds`.
- `modifiedAt` is updated for each successfully mutated rule.

Before:

```json
{
  "sensors": {
    "camera": ["rgb-main"]
  }
}
```

After adding `["rgb-side", "rgb-main"]`:

```json
{
  "sensors": {
    "camera": ["rgb-main", "rgb-side"]
  }
}
```

Status codes:

- `200 OK` with `BulkOperationResult`.
- `400 Bad Request` for missing ids, empty `sensorName`, empty values, duplicate request values, malformed JSON, or missing body.
- `503 Service Unavailable` when Elasticsearch access fails.

### PATCH /rules/sensors/remove

Removes sensor values from one or more rules without replacing the whole `sensors` object.

Query parameters:

- `ids`: comma-separated rule ids.

Request body:

```json
{
  "sensorName": "camera",
  "values": ["rgb-backup"]
}
```

Example:

```bash
curl -X PATCH "http://localhost:8080/rules/sensors/remove?ids=rule-001" \
  -H "Content-Type: application/json" \
  -d '{
    "sensorName": "camera",
    "values": ["rgb-backup"]
  }'
```

Behavior:

- Removing a value that is not present is a no-op for that rule.
- Removing from a sensor key that does not exist is a no-op for that rule.
- If the last value under a sensor key is removed, the sensor key is removed.
- Missing rule ids are reported in `failedIds`.
- `modifiedAt` is updated for each successfully processed existing rule.

Before:

```json
{
  "sensors": {
    "camera": ["rgb-main", "rgb-backup"]
  }
}
```

After removing `["rgb-backup"]`:

```json
{
  "sensors": {
    "camera": ["rgb-main"]
  }
}
```

Status codes:

- `200 OK` with `BulkOperationResult`.
- `400 Bad Request` for missing ids, empty `sensorName`, empty values, duplicate request values, malformed JSON, or missing body.
- `503 Service Unavailable` when Elasticsearch access fails.

### DELETE /rules/{id}

Deletes one rule by `_id`.

Example:

```bash
curl -X DELETE "http://localhost:8080/rules/rule-001"
```

Status codes:

- `204 No Content` when the rule was deleted.
- `404 Not Found` when the id does not exist.
- `503 Service Unavailable` when Elasticsearch access fails.

## Elasticsearch Behavior

The repository stores each rule in the configured rules index using `_id` as the Elasticsearch document id. Duplicate checks use the exact `ruleName.keyword` value.

Writes use `Refresh.WaitFor`, so test and follow-up reads can observe saved changes after the Elasticsearch refresh completes.

## Test Coverage

The route, service, validation, and converter behavior is covered by `tests/rules-api-tests`.

Run:

```bash
dotnet test tests/rules-api-tests/ImagingPipeline.Rules.Api.Tests.csproj
```
