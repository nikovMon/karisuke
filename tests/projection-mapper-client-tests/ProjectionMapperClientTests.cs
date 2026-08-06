using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Net.Http.Json;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient.Tests.Fakes;
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

        var result = await client.MapAsync("image-1", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] });

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
            () => client.MapAsync("image-1", new ProjectionMapperRequestDto { Coordinates = [] }));
    }

    [Fact]
    public async Task MapAsyncThrowsWhenTransportFails()
    {
        var handler = new FakeHttpMessageHandler(_ => throw new HttpRequestException("connection refused"));
        var client = CreateClient(handler);

        await Assert.ThrowsAsync<ProjectionMapperClientException>(
            () => client.MapAsync("image-1", new ProjectionMapperRequestDto { Coordinates = [] }));
    }

    [Fact]
    public async Task MapAsyncPropagatesCancellationInsteadOfWrappingItAsAClientException()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[1, 2]] })
        });
        var client = CreateClient(handler);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.MapAsync("image-1", new ProjectionMapperRequestDto { Coordinates = [[1, 2]] }, cts.Token));
    }

    [Fact]
    public async Task ProcessBatchAsyncUsesImageToGroundEndpoint()
    {
        var handler = new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[31, 32]] })
        });
        var client = CreateClient(handler);

        var result = await client.ProcessBatchAsync("image-2", [[1, 2]]);

        Assert.Single(result);
        Assert.Equal([31, 32], result[0]);
        Assert.Equal("/flare/i2g-by-id", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task LogicalOperationCreatesInternalSpanWithOverlayOnlyOnTheSpan()
    {
        Activity? completed = null;
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "FindAir.ProjectionMapper",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => completed = activity
        };
        ActivitySource.AddActivityListener(listener);
        var client = CreateClient(new FakeHttpMessageHandler(_ =>
        {
            var content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[1, 2]] });
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
        }));

        await client.MapAsync(
            "overlay-with-high-cardinality-id",
            new ProjectionMapperRequestDto { Coordinates = [[1, 2], [3, 4]] });

        var span = Assert.IsType<Activity>(completed);
        Assert.Equal(ActivityKind.Internal, span.Kind);
        Assert.Equal("projection_mapper ground_to_image", span.OperationName);
        Assert.Equal(
            "overlay-with-high-cardinality-id",
            span.GetTagItem("findair.image.id"));
        Assert.Equal(2, span.GetTagItem("projection_mapper.batch.size"));
        Assert.Equal("POST", span.GetTagItem("http.request.method"));
        Assert.Equal("http://projection-mapper.test/flare/g2i-by-id", span.GetTagItem("url.full"));
        Assert.Equal("/flare/g2i-by-id", span.GetTagItem("url.path"));
        Assert.Equal("/flare/g2i-by-id", span.GetTagItem("http.route"));
        Assert.Equal("projection-mapper.test", span.GetTagItem("server.address"));
    }

    [Fact]
    public async Task DependencyMetricsUseBoundedDimensionsAndExcludeOverlayId()
    {
        var measurements = new ConcurrentQueue<MetricMeasurement>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == TelemetrySourceNames.Dependencies)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            measurements.Enqueue(new MetricMeasurement(instrument.Name, value, tags.ToArray())));
        listener.Start();

        var client = CreateClient(new FakeHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new ProjectionMapperResponseDto { Coordinates = [[1, 2]] })
        }));
        await client.MapAsync(
            "overlay-must-not-be-a-metric-label",
            new ProjectionMapperRequestDto { Coordinates = [[1, 2], [3, 4]] });

        Assert.NotEmpty(measurements);
        Assert.DoesNotContain(
            measurements.SelectMany(static measurement => measurement.Tags),
            static tag => tag.Key.Contains("overlay", StringComparison.OrdinalIgnoreCase) ||
                          Equals(tag.Value, "overlay-must-not-be-a-metric-label"));
        Assert.Contains(measurements, static measurement =>
            measurement.Name == "findair.dependency.batch.size" &&
            measurement.Value == 2 &&
            measurement.Tags.Any(tag =>
                tag.Key == "findair.item" &&
                Equals(tag.Value, "ground_point")) &&
            measurement.Tags.Any(tag =>
                tag.Key == "findair.dependency.operation" &&
                Equals(tag.Value, "ground_to_image")));
        Assert.Contains(measurements, static measurement =>
            measurement.Name == "findair.dependency.operations" &&
            measurement.Value == 1);
        Assert.Contains(measurements, static measurement =>
            measurement.Name == "findair.dependency.payload.size" &&
            measurement.Value > 0 &&
            measurement.Tags.Any(tag =>
                tag.Key == "findair.direction" &&
                Equals(tag.Value, "ingress")));
    }

    private static ProjectionMapperClient CreateClient(FakeHttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://projection-mapper.test") };
        var options = new ProjectionMapperOptions
        {
            Host = "http://projection-mapper.test",
            Endpoints = new Dictionary<string, string>
            {
                [ProjectionMapperEndpointKeys.G2IMultiPoints] = "/flare/g2i-by-id",
                [ProjectionMapperEndpointKeys.I2GById] = "/flare/i2g-by-id"
            },
            SendingSystem = "flare"
        };
        return new ProjectionMapperClient(httpClient, Options.Create(options));
    }

    private sealed record MetricMeasurement(
        string Name,
        long Value,
        KeyValuePair<string, object?>[] Tags);
}
