# Projection Mapper client

Typed HTTP client for ground-to-image and image-to-ground mapping. Register it with
`AddProjectionMapperClient(configuration)`. Configure `ProjectionMapper:Host`,
`Endpoints`, `SendingSystem`, `UseCache`, and `TimeoutSeconds`. Deployment values
override the checked-in application examples. One call makes one HTTP request;
this library does not add retries or health probes.

`ProjectionMapperClientException` exposes `Kind`, `Code`, `Operation`, and optional
`HttpStatus`. HTTP 408/429/5xx, transport failures, and request timeouts are
`Transient`. HTTP 401/403 and invalid endpoint configuration are `Configuration`.
Malformed JSON and missing, empty, short, or non-finite returned coordinates are
`DependencyResponse`. Other HTTP statuses and unexpected failures are `Unknown`;
they are not evidence that source data is invalid. Caller cancellation propagates.

Exception messages contain safe summaries, not HTTP response bodies. Inner
exceptions remain available for debugging but are not recorded in dependency
span events. Legacy string constructors remain compatible and classify as
`Unknown`. Applications choose retry policy from typed fields rather than parsing
messages; non-transient failures require review by default.

Run `dotnet test tests/projection-mapper-client-tests/ImagingPipeline.ProjectionMapperClient.Tests.csproj`
from the repository root.

Endpoint values may be HTTP/HTTPS URLs or paths resolved against the configured
host. A rooted path such as `/flare/g2i-by-id` starts at the host root; a relative
path such as `flare/g2i-by-id` uses the base URL's path. Rooted paths remain HTTP
paths on both Windows and Linux. Explicit non-HTTP schemes are rejected before
sending a request.

Endpoint tests exercise rooted, relative and absolute URLs for all three mapping
operations and must pass on Linux as well as Windows. Other fixtures use complete
URLs, including the dynamically allocated mock-server port in integration tests.
