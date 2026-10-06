using ImagingPipeline.Observability;
using System.Net;
using System.Net.Sockets;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.UnifiedGateway.Dispatch;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class HttpDispatchTransportTests
{
    [Fact]
    public async Task RequestUsesCatalogEndpointMethodAndHeadersWithPayloadAndIdempotencyKey()
    {
        var pipeline = HttpPipeline("algo", new HttpTransportOptions
        {
            Endpoint = "https://algo.invalid/mission/upsert/?mode=integ",
            Method = "PUT",
            Headers = new Dictionary<string, string>
            {
                ["Accept"] = "application/json",
                ["X-Configured"] = "configured",
                ["tenantId"] = "configured-tenant",
                ["Idempotency-Key"] = "configured-key"
            }
        });
        var handler = new StubHandler(HttpStatusCode.OK);
        var unit = Unit(pipeline, payload: Payload(new Dictionary<string, string> { ["tenantId"] = "tenant-a" }));

        var outcome = await CreateTransport(handler, pipeline).SendAsync(unit, CancellationToken.None);

        Assert.Equal(DispatchStatus.Delivered, outcome.Status);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Put, request.Method);
        Assert.Equal("https://algo.invalid/mission/upsert/?mode=integ", request.Uri);
        Assert.Equal("""{"taskId":"task-a"}""", request.Body);
        Assert.Equal("application/json", request.ContentType);
        Assert.Equal(["application/json"], request.Headers["Accept"]);
        Assert.Equal(["configured"], request.Headers["X-Configured"]);
        Assert.Equal(["tenant-a"], request.Headers["tenantId"]);
        Assert.Equal([unit.DispatchId], request.Headers["Idempotency-Key"]);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, DispatchStatus.Delivered, TelemetryErrorCategory.None)]
    [InlineData(HttpStatusCode.Created, DispatchStatus.Delivered, TelemetryErrorCategory.None)]
    [InlineData(HttpStatusCode.NoContent, DispatchStatus.Delivered, TelemetryErrorCategory.None)]
    [InlineData(HttpStatusCode.RequestTimeout, DispatchStatus.Retryable, TelemetryErrorCategory.Timeout)]
    [InlineData(HttpStatusCode.TooManyRequests, DispatchStatus.Retryable, TelemetryErrorCategory.Unavailable)]
    [InlineData(HttpStatusCode.ServiceUnavailable, DispatchStatus.Retryable, TelemetryErrorCategory.Unavailable)]
    [InlineData(HttpStatusCode.InternalServerError, DispatchStatus.Retryable, TelemetryErrorCategory.Dependency)]
    [InlineData(HttpStatusCode.BadGateway, DispatchStatus.Retryable, TelemetryErrorCategory.Dependency)]
    [InlineData(HttpStatusCode.BadRequest, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    [InlineData(HttpStatusCode.Unauthorized, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    [InlineData(HttpStatusCode.NotFound, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    [InlineData(HttpStatusCode.Conflict, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    [InlineData(HttpStatusCode.UnprocessableEntity, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    [InlineData(HttpStatusCode.Found, DispatchStatus.Rejected, TelemetryErrorCategory.Validation)]
    public async Task StatusCodeMapsToOutcomeAndCategory(
        HttpStatusCode statusCode,
        DispatchStatus expected,
        TelemetryErrorCategory expectedError)
    {
        var pipeline = HttpPipeline("algo");

        var outcome = await CreateTransport(new StubHandler(statusCode), pipeline)
            .SendAsync(Unit(pipeline), CancellationToken.None);

        Assert.Equal(expected, outcome.Status);
        Assert.Equal(expectedError, outcome.Error);
        Assert.Equal((int)statusCode, outcome.StatusCode);
    }

    [Fact]
    public async Task ConnectionFailureIsRetryable()
    {
        var pipeline = HttpPipeline("algo");
        var handler = new StubHandler(HttpStatusCode.OK)
        {
            Failure = new HttpRequestException(HttpRequestError.ConnectionError, "refused", new SocketException())
        };

        var outcome = await CreateTransport(handler, pipeline).SendAsync(Unit(pipeline), CancellationToken.None);

        Assert.Equal(DispatchStatus.Retryable, outcome.Status);
        Assert.Equal(TelemetryErrorCategory.Connection, outcome.Error);
        Assert.Null(outcome.StatusCode);
        Assert.IsType<HttpRequestException>(outcome.Exception);
    }

    [Fact]
    public async Task CatalogTimeoutIsRetryable()
    {
        var pipeline = HttpPipeline("algo", new HttpTransportOptions
        {
            Endpoint = "https://algo.invalid/work",
            TimeoutSeconds = 1
        });
        var handler = new StubHandler(HttpStatusCode.OK) { Delay = TimeSpan.FromSeconds(30) };

        var outcome = await CreateTransport(handler, pipeline).SendAsync(Unit(pipeline), CancellationToken.None);

        Assert.Equal(DispatchStatus.Retryable, outcome.Status);
        Assert.Equal(TelemetryErrorCategory.Timeout, outcome.Error);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var pipeline = HttpPipeline("algo");
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => CreateTransport(new StubHandler(HttpStatusCode.OK) { Delay = TimeSpan.FromSeconds(30) }, pipeline)
                .SendAsync(Unit(pipeline), cancellation.Token));
    }

    [Fact]
    public async Task UnitForPipelineWithoutAnEnabledHttpEndpointIsRejected()
    {
        var rabbit = RabbitMqPipeline("asd");
        var disabled = HttpPipeline("disabled") with { Enabled = false };
        var handler = new StubHandler(HttpStatusCode.OK);
        var transport = CreateTransport(handler, rabbit, disabled);

        Assert.Equal(DispatchStatus.Rejected, (await transport.SendAsync(Unit(rabbit), CancellationToken.None)).Status);
        Assert.Equal(DispatchStatus.Rejected, (await transport.SendAsync(Unit(disabled), CancellationToken.None)).Status);
        Assert.Empty(handler.Requests);
    }

    private static HttpDispatchTransport CreateTransport(StubHandler handler, params PipelineDefinition[] pipelines) =>
        new(CreateCatalog(pipelines), new StubClientFactory(handler));

    private sealed record RecordedRequest(
        HttpMethod Method,
        string Uri,
        string Body,
        string? ContentType,
        Dictionary<string, string[]> Headers);

    private sealed class StubHandler(HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<RecordedRequest> Requests { get; } = [];
        public TimeSpan Delay { get; init; }
        public Exception? Failure { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            if (Failure is not null)
            {
                throw Failure;
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.ToString(),
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Content.Headers.ContentType?.MediaType,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            return new HttpResponseMessage(statusCode);
        }
    }

    private sealed class StubClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(HttpDispatchTransport.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }
}
