using System.Net;
using System.Net.Http.Json;
using ImagingPipeline.ProjectionMapperClient.Tests.Fakes;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient.Tests;

public sealed class ProjectionMapperEndpointTests
{
    [Theory]
    [InlineData(ProjectionMapperEndpointKeys.G2IMultiPoints, "/flare/g2i-by-id", "https://projection.example/flare/g2i-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.G2IMultiPoints, "flare/g2i-by-id", "https://projection.example/base/flare/g2i-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.G2IMultiPoints, "https://other.example/flare/g2i-by-id", "https://other.example/flare/g2i-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.I2GById, "/flare/i2g-by-id", "https://projection.example/flare/i2g-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.I2GById, "flare/i2g-by-id", "https://projection.example/base/flare/i2g-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.I2GById, "https://other.example/flare/i2g-by-id", "https://other.example/flare/i2g-by-id")]
    [InlineData(ProjectionMapperEndpointKeys.I2GByRegistration, "/flare/i2g-by-registration", "https://projection.example/flare/i2g-by-registration")]
    [InlineData(ProjectionMapperEndpointKeys.I2GByRegistration, "flare/i2g-by-registration", "https://projection.example/base/flare/i2g-by-registration")]
    [InlineData(ProjectionMapperEndpointKeys.I2GByRegistration, "https://other.example/flare/i2g-by-registration", "https://other.example/flare/i2g-by-registration")]
    public async Task EndpointIsResolvedAgainstHttpBaseForEveryOperation(string operation, string endpoint, string expectedPath)
    {
        var handler = SuccessfulHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://projection.example/base/") };
        var client = CreateClient(httpClient, operation, endpoint);

        var result = await ExecuteAsync(client, operation);

        Assert.Single(result);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, handler.LastRequest.Method);
        var expectedQuery = operation == ProjectionMapperEndpointKeys.I2GByRegistration
            ? string.Empty : "?overlayId=example-image&useCache=false";
        Assert.Equal(expectedPath + expectedQuery, handler.LastRequest.RequestUri!.AbsoluteUri);
    }

    [Theory]
    [InlineData(ProjectionMapperEndpointKeys.G2IMultiPoints, "file:///projection/map")]
    [InlineData(ProjectionMapperEndpointKeys.G2IMultiPoints, "ftp://other.example/projection/map")]
    [InlineData(ProjectionMapperEndpointKeys.I2GById, "file:///projection/map")]
    [InlineData(ProjectionMapperEndpointKeys.I2GById, "ftp://other.example/projection/map")]
    [InlineData(ProjectionMapperEndpointKeys.I2GByRegistration, "file:///projection/map")]
    [InlineData(ProjectionMapperEndpointKeys.I2GByRegistration, "ftp://other.example/projection/map")]
    public async Task ExplicitUnsupportedSchemeIsRejectedBeforeSending(string operation, string endpoint)
    {
        var handler = SuccessfulHandler();
        using var httpClient = new HttpClient(handler) { BaseAddress = new Uri("https://projection.example/base/") };
        var client = CreateClient(httpClient, operation, endpoint);

        var failure = await Assert.ThrowsAsync<ProjectionMapperClientException>(() => ExecuteAsync(client, operation));

        Assert.Equal(ProjectionMapperFailureKind.Configuration, failure.Kind);
        Assert.Equal("projection_mapper_endpoint_invalid", failure.Code);
        Assert.Equal(operation, failure.Operation);
        Assert.Null(handler.LastRequest);
    }

    private static FakeHttpMessageHandler SuccessfulHandler() => new(_ => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[1, 2]] })
    });

    private static ProjectionMapperClient CreateClient(HttpClient httpClient, string operation, string endpoint) =>
        new(httpClient, Options.Create(new ProjectionMapperOptions
        {
            SendingSystem = "test",
            UseCache = false,
            Endpoints = new Dictionary<string, string> { [operation] = endpoint }
        }));

    private static Task<IReadOnlyList<IReadOnlyList<double>>> ExecuteAsync(ProjectionMapperClient client, string operation) =>
        operation switch
        {
            ProjectionMapperEndpointKeys.G2IMultiPoints => client.MapAsync("example-image", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }),
            ProjectionMapperEndpointKeys.I2GById => client.ProcessBatchAsync("example-image", [[1, 2]]),
            ProjectionMapperEndpointKeys.I2GByRegistration => client.ProcessBatchByRegistrationAsync(
                "example-image", [[1, 2]], "MSP", "grid://example"),
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
}
