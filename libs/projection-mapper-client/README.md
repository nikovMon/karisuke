# Projection Mapper client

Typed HTTP client for ground-to-image and image-to-ground mapping. Register it with
`AddProjectionMapperClient(configuration)`. Configure `ProjectionMapper:Host`,
`Endpoints`, `SendingSystem`, `UseCache`, and `TimeoutSeconds`. Deployment values
override the checked-in application examples. One call makes one HTTP request;
this library does not add retries or health probes.

Endpoint paths resolve against the HTTP client base address (`ProjectionMapper:Host`).
A leading `/` selects a path at the host root; paths without it are relative to
the base path. Absolute HTTP/HTTPS endpoints are also supported. Other URI schemes
and relative endpoints without a base address fail as configuration errors.

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
