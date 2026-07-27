# Elasticsearch Client

`libs/elasticsearch-client` contains reusable Elasticsearch wiring and generic document helpers for application components.

The registered NEST client uses a `System.Text.Json` source serializer so shared DTO attributes and string enums keep the same JSON representation in APIs and Elasticsearch.

It exposes two clients through DI:

- `IElasticClient`: the raw NEST client for advanced Elasticsearch operations.
- `IElasticsearchDocumentClient`: a small generic wrapper for common document operations and reusable sensor/geography searches.

## Source Layout

- `ElasticsearchDocumentClient.cs`: client interface, implementation, and client exception.
- `ElasticsearchQueries.cs`: search requests, filters, results, and query JSON builder.
- `ElasticsearchOptions.cs`: configuration options and JSON serialization.
- `ElasticsearchExtensions.cs`: dependency injection registration.

## Registration

```csharp
builder.Services.AddElasticsearchClient(builder.Configuration);
```

Configuration:

```json
{
  "Elasticsearch": {
    "Uri": "http://localhost:9200",
    "Index": "rules",
    "TimeoutSeconds": 30,
    "Username": "optional-user",
    "Password": "optional-password"
  }
}
```

Validation:

- `Uri` must be absolute `http` or `https`.
- `Index` cannot be empty.
- `TimeoutSeconds` must be greater than zero.
- `Username` and `Password` must be configured together.

## Generic Functions

### SearchAsync

Runs a generic Elasticsearch search from simple filter objects.

```csharp
var results = await client.SearchAsync<MyDocument>(new ElasticsearchSearchRequest
{
    IndexName = "rules",
    Size = 100,
    TermFilters =
    [
        new ElasticsearchTermFilter
        {
            Field = "isActive",
            Value = true
        }
    ],
    TermsFilters =
    [
        new ElasticsearchTermsFilter
        {
            Field = "algorithmName.keyword",
            Values = ["FindAir"]
        }
    ]
});
```

When no filters are supplied, the query uses `match_all`.

### SearchBySensorAsync

Searches documents by sensor name and one or more sensor values.

```csharp
var results = await client.SearchBySensorAsync<MyDocument>(new ElasticsearchSensorSearchRequest
{
    IndexName = "rules",
    SensorName = "camera",
    Values = ["rgb-main", "rgb-backup"]
});
```

Default field path:

```text
sensors.{sensorName}.keyword
```

For another component with a different mapping:

```csharp
var results = await client.SearchBySensorAsync<MyDocument>(new ElasticsearchSensorSearchRequest
{
    IndexName = "events",
    SensorRootField = "metadata.sensors",
    SensorName = "thermal",
    Values = ["th-1"],
    KeywordSuffix = ""
});
```

That searches:

```text
metadata.sensors.thermal
```

### SearchByGeoShapeAsync

Searches documents by an Elasticsearch `geo_shape` query.

```csharp
using var shape = JsonDocument.Parse("""
{
  "type": "Polygon",
  "coordinates": [[[34.7,32.0],[34.9,32.0],[34.9,32.2],[34.7,32.2],[34.7,32.0]]]
}
""");

var results = await client.SearchByGeoShapeAsync<MyDocument>(new ElasticsearchGeoShapeSearchRequest
{
    IndexName = "rules",
    Field = "locationGeoJson",
    Shape = shape.RootElement.Clone(),
    Relation = ElasticsearchGeoShapeRelation.Intersects
});
```

Supported relations:

- `Intersects`
- `Disjoint`
- `Within`
- `Contains`

### Search By Sensor And Geography

Use `SearchAsync` when a component needs multiple filters in the same query.

```csharp
using var shape = JsonDocument.Parse("""
{
  "type": "Point",
  "coordinates": [34.8, 32.1]
}
""");

var results = await client.SearchAsync<MyDocument>(new ElasticsearchSearchRequest
{
    IndexName = "rules",
    SensorFilters =
    [
        new ElasticsearchSensorFilter
        {
            SensorName = "camera",
            Values = ["rgb-main"]
        }
    ],
    GeoShapeFilters =
    [
        new ElasticsearchGeoShapeFilter
        {
            Field = "locationGeoJson",
            Shape = shape.RootElement.Clone(),
            Relation = ElasticsearchGeoShapeRelation.Intersects
        }
    ]
});
```

### GetAsync

Gets one document by id.

```csharp
var rule = await client.GetAsync<MyDocument>("rules", "rule-001");
```

Returns `null` when the document is missing.

Use `GetDocumentAsync` when callers also need the Elasticsearch metadata id:

```csharp
var document = await client.GetDocumentAsync<MyDocument>("rules", "rule-001");
var id = document?.Id;
var source = document?.Source;
```

### SearchDocumentsAsync

Runs a NEST search and returns each hit with its Elasticsearch metadata id.

```csharp
var documents = await client.SearchDocumentsAsync<MyDocument>(
    descriptor => descriptor
        .Index("rules")
        .Size(10)
        .Query(query => query.Term("isActive", true)));
```

### IndexAsync

Creates or replaces a document.

```csharp
await client.IndexAsync("rules", "rule-001", document);
```

The returned value is the Elasticsearch document id. Pass `allowGeneratedId: true` with a blank id when Elasticsearch should generate the id:

```csharp
var generatedId = await client.IndexAsync(
    "rules",
    id: null,
    document,
    waitForRefresh: false,
    allowGeneratedId: true);
```

By default this waits for Elasticsearch refresh:

```csharp
await client.IndexAsync("rules", "rule-001", document, waitForRefresh: true);
```

### DeleteAsync

Deletes one document.

```csharp
var deleted = await client.DeleteAsync<MyDocument>("rules", "rule-001");
```

Returns `false` when the document is already missing.

## Errors

Invalid request objects throw normal .NET exceptions such as `ArgumentException` and `ArgumentOutOfRangeException`.

Elasticsearch failures throw `ElasticsearchClientException` with the operation name, response status when available, and a server reason capped at 512 characters. Raw response bodies and NEST debug dumps are never copied into exceptions; an underlying transport exception is retained as `InnerException` when available.

## Observability

Get, search, index, and delete calls emit `ImagingPipeline.Elasticsearch` client spans plus centralized dependency metrics. The spans follow the [OpenTelemetry Elasticsearch semantic conventions](https://opentelemetry.io/docs/specs/semconv/db/elasticsearch/):

- span kind is `CLIENT`, with display name `{operation} {index}` when the index is known;
- `db.system.name` is `elasticsearch`, `db.operation.name` identifies the operation, and `db.collection.name` contains the index;
- `http.request.method`, a scrubbed absolute `url.full`, and `db.response.status_code` are populated from the Elasticsearch response metadata;
- 4xx/5xx responses use their status code as `error.type` on both spans and operation/duration metrics.

The safe `url.full` omits the query string and redacts URL credentials. Query text, request/response bodies, authentication data, and arbitrary headers are never added to telemetry. Production registration leaves direct streaming enabled, so observability does not require NEST to buffer payload bodies.

Document IDs are attached only to spans; metric dimensions remain bounded to dependency, operation, outcome, error type, and item type. Payload-size and batch-size histograms intentionally omit outcome and error dimensions because those measurements can be recorded before the operation completes. Elasticsearch byte sizes are recorded only when the client transport already exposes buffered byte arrays; direct streaming remains enabled, so bodies are never buffered solely for metrics.

Cancellation requested by the caller is reported as `cancelled`; transport timeouts and dependency failures are recorded separately. The host application must register `ImagingPipeline.Observability` for OTLP export.

## Testing

The client has a dedicated test project:

```bash
dotnet test tests/elasticsearch-client-tests/ImagingPipeline.ElasticsearchClient.Tests.csproj
```

The tests use query JSON assertions and NEST's `InMemoryConnection`, so they do not require a live Elasticsearch container.
