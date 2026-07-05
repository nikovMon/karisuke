using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.ProjectionMapperClient.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient.Tests;

public sealed class ProjectionMapperClientTests
{
    [Fact]
    public async Task MapAsyncReturnsCoordinatesOnSuccess()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[1, 2], [3, 4]] })
        });
        var client = CreateClient(handler);

        var result = await client.MapAsync("image-1", new ProjectionMapperRequestDto { GroundPoints = ParseGroundPoints("[[1,2]]") });

        Assert.Equal(2, result.Count);
        Assert.Equal([3, 4], result[1]);
        Assert.NotNull(handler.LastRequest);
        Assert.Equal("/flare/g2i-by-id", handler.LastRequest!.RequestUri!.AbsolutePath);
        Assert.Contains("overlayId=image-1", handler.LastRequest.RequestUri.Query);
        Assert.Contains("useCache=false", handler.LastRequest.RequestUri.Query);
    }

    [Fact]
    public async Task MapAsyncThrowsWhenHttpStatusIsNotSuccess()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("image-1", new ProjectionMapperRequestDto { GroundPoints = ParseGroundPoints("[]") }));
    }

    [Fact]
    public async Task MapAsyncThrowsWhenTransportFails()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("image-1", new ProjectionMapperRequestDto { GroundPoints = ParseGroundPoints("[]") }));
    }

    private static JsonElement ParseGroundPoints(string json) => JsonDocument.Parse(json).RootElement;

    private static ProjectionMapperClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://projection-mapper.test") };
        var options = new ProjectionMapperOptions
        {
            Host = "http://projection-mapper.test",
            Endpoints = new Dictionary<string, string> { [ProjectionMapperEndpointKeys.G2IMultiPoints] = "/flare/g2i-by-id" },
            SendingSystem = "flare"
        };
        return new ProjectionMapperClient(httpClient, Options.Create(options), NullLogger<ProjectionMapperClient>.Instance);
    }
}
