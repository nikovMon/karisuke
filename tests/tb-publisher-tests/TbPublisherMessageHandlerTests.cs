using System.Text;
using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Observability;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.MessageHandling;
using ImagingPipeline.TbPublisher.Processing;
using ImagingPipeline.TbPublisher.Tests.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using OpenTelemetry;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TbPublisherMessageHandlerTests
{
    private static readonly byte[] ValidBody = Encoding.UTF8.GetBytes("""
    {
      "taskId": "msg-1",
      "ruleId": "rule-1",
      "algorithmName": ["FindAir", "Rpn"],
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
      "photoTime": "2026-07-27T10:00:00Z",
      "sensorType": "EO",
      "imageUrl": "/images/image-1.tiff",
      "imageWidth": 4096,
      "imageHeight": 3072,
      "bestResolution": 100,
      "sensorName": "sensor-1",
      "areaOfInterest": "region-alpha",
      "tilingConfigs": [
        { "tileSizeWidth": 512, "tileSizeHeight": 384, "tileOverlapWidth": 32, "tileOverlapHeight": 24 },
        { "tileSizeWidth": 256, "tileSizeHeight": 128, "tileOverlapWidth": 16, "tileOverlapHeight": 8 }
      ]
    }
    """);

    private static readonly IReadOnlyList<IReadOnlyList<double>> Coordinates =
        [[0, 0], [1, 0], [1, 1], [0, 1]];

    [Fact]
    public async Task HandleAsyncPublishesOneMessagePerTilingConfig()
    {
        var projectionClient = FakeProjectionMapperClient.ReturningSuccess(Coordinates);
        var publisher = new FakeRabbitMqPublisher();
        var handler = CreateHandler(projectionClient, publisher);

        var input = RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1") with
        {
            CorrelationId = "correlation-1",
            Headers = new Dictionary<string, object?>
            {
                ["x-pipeline-start-unix-ms"] = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(),
                ["business-header"] = "preserved"
            }
        };

        var result = await handler.HandleAsync(input);

        Assert.True(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.Equal(2, publisher.PublishedToOutput.Count);

        var outputs = publisher.PublishedToOutput
            .Select(envelope => JsonSerializer.Deserialize<TbPublisherOutputMessageDto>(
                envelope.Body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .ToList();
        Assert.Equal("tenant-1", outputs[0].MissionMetadata.TenantId);
        Assert.Equal("image-1", outputs[0].MissionMetadata.Overlay.ImageId);
        Assert.Equal(
            [AlgorithmName.FindAir, AlgorithmName.Rpn],
            outputs[0].MissionMetadata.Overlay.AlgorithmNames);

        Assert.Equal(512, outputs[0].ModelMetadata.TbCropSizeX);
        Assert.Equal(384, outputs[0].ModelMetadata.TbCropSizeY);
        Assert.Equal(32, outputs[0].ModelMetadata.OverlapWidth);
        Assert.Equal(24, outputs[0].ModelMetadata.OverlapHeight);

        Assert.Equal(256, outputs[1].ModelMetadata.TbCropSizeX);
        Assert.Equal(128, outputs[1].ModelMetadata.TbCropSizeY);
        Assert.Equal(16, outputs[1].ModelMetadata.OverlapWidth);
        Assert.Equal(8, outputs[1].ModelMetadata.OverlapHeight);

        Assert.Equal(outputs[0].MissionMetadata.MissionId, outputs[1].MissionMetadata.MissionId);
        Assert.Equal("msg-1", outputs[0].TaskId);
        Assert.Equal(outputs[0].TaskId, outputs[1].TaskId);
        Assert.StartsWith("POLYGON", outputs[0].FocusedPxWkt);
        Assert.Equal("image-1", projectionClient.LastOverlayId);
        Assert.All(publisher.PublishedToOutput, envelope =>
        {
            Assert.Equal("correlation-1", envelope.CorrelationId);
            Assert.NotNull(envelope.Headers);
            Assert.Equal("preserved", envelope.Headers["business-header"]);
            Assert.True(envelope.Headers.ContainsKey("x-pipeline-start-unix-ms"));
        });
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenMessageIsInvalid()
    {
        var handler = CreateHandler(FakeProjectionMapperClient.ReturningSuccess(Coordinates), new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8("{}"));

        Assert.False(result.IsSuccess);
        Assert.Null(result.OutputBody);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task HandleAsyncCreatesAggregateValidationGeometryProjectionAndBuildSpans()
    {
        var handler = CreateHandler(
            FakeProjectionMapperClient.ReturningSuccess(Coordinates),
            new FakeRabbitMqPublisher());
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.TbPublisher);
        using var testRoot = new Activity("tb-publisher-test").Start();

        var result = await handler.HandleAsync(
            RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "telemetry-message"));

        var testActivities = activities.Activities
            .Where(activity => activity.TraceId == testRoot.TraceId)
            .ToArray();
        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["tb_publisher.validate", "tb_publisher.geometry", "tb_publisher.projection", "tb_publisher.build"],
            testActivities.Select(activity => activity.DisplayName).ToArray());
        Assert.All(testActivities, activity => Assert.Equal(ActivityKind.Internal, activity.Kind));
        Assert.All(testActivities, activity => Assert.Equal(ActivityStatusCode.Ok, activity.Status));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenProjectionMapperFails()
    {
        var handler = CreateHandler(
            FakeProjectionMapperClient.ThrowingFailure(new ProjectionMapperClientException("mapper down")),
            new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1"));

        Assert.False(result.IsSuccess);
        Assert.Contains("mapper down", result.Error);
    }

    [Fact]
    public async Task HandleAsyncMarksBuildSerializationFailureOnSpan()
    {
        var handler = CreateHandler(
            FakeProjectionMapperClient.ReturningSuccess(Coordinates),
            new FakeRabbitMqPublisher(),
            new ThrowingOutputMessageBuilder());
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.TbPublisher);
        using var testRoot = new Activity("tb-publisher-test").Start();

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(
            RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1")));

        var activity = Assert.Single(
            activities.Activities,
            activity => activity.TraceId == testRoot.TraceId
                        && activity.DisplayName == "tb_publisher.build");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("serialization", activity.GetTagItem(TelemetryAttributeNames.ErrorCategory));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenTilingConfigIsInvalid()
    {
        var invalidTilingBody = Encoding.UTF8.GetBytes("""
        {
          "taskId": "msg-1",
          "ruleId": "rule-1",
          "algorithmName": ["FindAir"],
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
          "photoTime": "2026-07-27T10:00:00Z",
          "sensorType": "EO",
          "imageUrl": "/images/image-1.tiff",
          "imageWidth": 4096,
          "imageHeight": 3072,
          "bestResolution": 100,
          "sensorName": "sensor-1",
          "areaOfInterest": "region-alpha",
          "tilingConfigs": [
            { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 512, "tileOverlapHeight": 0 }
          ]
        }
        """);
        var handler = CreateHandler(FakeProjectionMapperClient.ReturningSuccess(Coordinates), new FakeRabbitMqPublisher());

        var result = await handler.HandleAsync(
            RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(invalidTilingBody), "msg-1"));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task HandleAsyncPropagatesCancellationTokenToItsDependencies()
    {
        var projectionClient = FakeProjectionMapperClient.ReturningSuccess(Coordinates);
        var publisher = new FakeRabbitMqPublisher();
        var handler = CreateHandler(projectionClient, publisher);
        using var cts = new CancellationTokenSource();

        await handler.HandleAsync(
            RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1"),
            cts.Token);

        Assert.Equal(cts.Token, projectionClient.LastCancellationToken);
        Assert.Equal(cts.Token, publisher.LastCancellationToken);
    }

    [Fact]
    public async Task HandleAsyncPublishesAuthoritativeBoundedCorrelationBaggageAndRestoresAmbientState()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                [TelemetryAttributeNames.PipelineTaskId] = "spoofed-task",
                [TelemetryAttributeNames.PipelineRequestId] = "upstream-request",
                ["secret"] = "do-not-forward"
            });
            var publisher = new FakeRabbitMqPublisher();
            var handler = CreateHandler(FakeProjectionMapperClient.ReturningSuccess(Coordinates), publisher);

            var result = await handler.HandleAsync(
                RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1"));

            Assert.True(result.IsSuccess);
            Assert.Equal(2, publisher.BaggageSnapshots.Count);
            Assert.All(publisher.BaggageSnapshots, baggage =>
            {
                Assert.Equal("msg-1", baggage[TelemetryAttributeNames.PipelineTaskId]);
                Assert.Equal("upstream-request", baggage[TelemetryAttributeNames.PipelineRequestId]);
                Assert.Equal("image-1", baggage[TelemetryAttributeNames.PipelineImageId]);
                Assert.Equal("rule-1", baggage[TelemetryAttributeNames.PipelineRuleId]);
                Assert.Equal("tenant-1", baggage[TelemetryAttributeNames.PipelineTenantId]);
                Assert.Equal("FindAir,Rpn", baggage[TelemetryAttributeNames.PipelineAlgorithmName]);
                Assert.DoesNotContain("secret", baggage.Keys);
            });
            Assert.Equal("spoofed-task", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
            Assert.Equal("do-not-forward", Baggage.Current.GetBaggage("secret"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public async Task HandleAsyncThrowsAndLeavesEarlierTilingConfigsPublishedWhenAPublishFailsPartway()
    {
        var projectionClient = FakeProjectionMapperClient.ReturningSuccess(Coordinates);
        var publisher = FakeRabbitMqPublisher.ThatFailsAfter(successCount: 1);
        var handler = CreateHandler(projectionClient, publisher);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8(Encoding.UTF8.GetString(ValidBody), "msg-1")));

        Assert.Single(publisher.PublishedToOutput);
    }

    private static TbPublisherMessageHandler CreateHandler(
        IProjectionMapperClient projectionMapperClient,
        IRabbitMqPublisher publisher,
        ITbPublisherOutputMessageBuilder? outputMessageBuilder = null) =>
        new(
            new InputMessageValidator(),
            new TbPublisherGeometryConverter(),
            projectionMapperClient,
            outputMessageBuilder ?? new TbPublisherOutputMessageBuilder(),
            publisher,
            NullLogger<TbPublisherMessageHandler>.Instance);

    private sealed class ThrowingOutputMessageBuilder : ITbPublisherOutputMessageBuilder
    {
        public IReadOnlyList<TbPublisherOutputMessageDto> Map(
            GatewayOutputMessageDto message,
            string focusedPxWkt) =>
            throw new InvalidOperationException("Output serialization preparation failed.");
    }
}
