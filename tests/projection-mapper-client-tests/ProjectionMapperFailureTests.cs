using System.Net;
using ImagingPipeline.ProjectionMapperClient.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient.Tests;

public sealed class ProjectionMapperFailureTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"coordinates\":null}")]
    [InlineData("{\"coordinates\":[]}")]
    [InlineData("{\"coordinates\":[null]}")]
    [InlineData("{\"coordinates\":[[1]]}")]
    [InlineData("{\"coordinates\":[[1e999,2]]}")]
    public async Task InvalidResponseCoordinatesAreRejected(string body)
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body)
        });

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.DependencyResponse, failure.Kind);
        Assert.Equal("projection_mapper_invalid_coordinates", failure.Code);
        Assert.Equal(ProjectionMapperEndpointKeys.G2IMultiPoints, failure.Operation);
        Assert.Equal(200, failure.HttpStatus);
    }

    [Theory]
    [InlineData(408, ProjectionMapperFailureKind.Transient)]
    [InlineData(429, ProjectionMapperFailureKind.Transient)]
    [InlineData(500, ProjectionMapperFailureKind.Transient)]
    [InlineData(503, ProjectionMapperFailureKind.Transient)]
    [InlineData(401, ProjectionMapperFailureKind.Configuration)]
    [InlineData(403, ProjectionMapperFailureKind.Configuration)]
    [InlineData(400, ProjectionMapperFailureKind.Unknown)]
    [InlineData(404, ProjectionMapperFailureKind.Unknown)]
    [InlineData(422, ProjectionMapperFailureKind.Unknown)]
    public async Task HttpStatusIsClassifiedWithoutIncludingResponseBody(int status, ProjectionMapperFailureKind kind)
    {
        var client = CreateClient(_ => new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent("private-response-body")
        });

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(kind, failure.Kind);
        Assert.Equal(status, failure.HttpStatus);
        Assert.Equal(ProjectionMapperEndpointKeys.G2IMultiPoints, failure.Operation);
        Assert.DoesNotContain("private-response-body", failure.ToString());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TimeoutAndConnectionFailuresAreTransient(bool timeout)
    {
        var client = CreateClient(_ => throw (timeout
            ? new TaskCanceledException("private-transport-details")
            : new HttpRequestException("private-transport-details")));

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.Transient, failure.Kind);
        Assert.Equal(timeout ? "projection_mapper_timeout" : "projection_mapper_transport", failure.Code);
        Assert.DoesNotContain("private-transport-details", failure.Message);
    }

    [Fact]
    public async Task MalformedJsonIsDependencyResponseFailure()
    {
        var client = CreateClient(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("not-json-private-body")
        });

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.DependencyResponse, failure.Kind);
        Assert.Equal("projection_mapper_invalid_json", failure.Code);
        Assert.DoesNotContain("private-body", failure.Message);
    }

    [Theory]
    [InlineData("http://localhost/projection/g2i", "http://localhost/base/", "http://localhost/projection/g2i")]
    [InlineData("projection/g2i", "http://localhost/base/", "http://localhost/base/projection/g2i")]
    [InlineData("https://example.invalid/projection/g2i", "http://localhost/base/", "https://example.invalid/projection/g2i")]
    [InlineData("https://example.invalid/projection/g2i", null, "https://example.invalid/projection/g2i")]
    public async Task SupportedHttpEndpointsUseExpectedRequestUri(string endpoint, string? baseAddress, string expectedUri)
    {
        Uri? requestUri = null;
        var client = CreateClient(request =>
        {
            requestUri = request.RequestUri;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"coordinates\":[[1,2]]}")
            };
        }, endpoint, baseAddress);

        var result = await client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] });

        Assert.Single(result);
        Assert.NotNull(requestUri);
        Assert.Equal(expectedUri, requestUri.GetLeftPart(UriPartial.Path));
    }

    [Fact]
    public async Task RelativeEndpointWithoutBaseAddressIsConfigurationFailure()
    {
        var called = false;
        var client = CreateClient(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, "/projection/g2i", null);

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.Configuration, failure.Kind);
        Assert.Equal("projection_mapper_endpoint_invalid", failure.Code);
        Assert.False(called);
    }

    [Theory]
    [InlineData("")]
    [InlineData("file:///projection")]
    [InlineData("ftp://example.invalid/projection")]
    public async Task InvalidEndpointIsConfigurationFailureWithoutMakingRequest(string endpoint)
    {
        var called = false;
        var client = CreateClient(_ =>
        {
            called = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        }, endpoint);

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.Configuration, failure.Kind);
        Assert.False(called);
    }

    [Fact]
    public async Task UnexpectedFailureIsHeldAsUnknown()
    {
        var client = CreateClient(_ => throw new InvalidOperationException("private-runtime-details"));

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }));

        Assert.Equal(ProjectionMapperFailureKind.Unknown, failure.Kind);
        Assert.DoesNotContain("private-runtime-details", failure.Message);
    }

    private static ProjectionMapperClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        string endpoint = "http://localhost/projection/g2i",
        string? baseAddress = "http://localhost") =>
        new(new HttpClient(new FakeHttpMessageHandler(responder))
        {
            BaseAddress = baseAddress is null ? null : new Uri(baseAddress)
        },
            Options.Create(new ProjectionMapperOptions
            {
                SendingSystem = "test",
                Endpoints = new Dictionary<string, string>
                {
                    [ProjectionMapperEndpointKeys.G2IMultiPoints] = endpoint
                }
            }));
}
