using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OpenTelemetry;

namespace ImagingPipeline.TbConsumer.Tests;

public class TbMessageHandlerTests
{
    private readonly Mock<IProjectionMapperClient> _projectionMapperMock;
    private readonly Mock<IRabbitMqPublisher> _publisherMock;
    private readonly FakeTimeProvider _timeProvider;
    private readonly TbMessageHandler _handler;

    public TbMessageHandlerTests()
    {
        _projectionMapperMock = new Mock<IProjectionMapperClient>();
        _publisherMock = new Mock<IRabbitMqPublisher>();
        _timeProvider = new FakeTimeProvider(new DateTimeOffset(2026, 7, 15, 12, 0, 0, TimeSpan.Zero));

        _handler = new TbMessageHandler(
            _projectionMapperMock.Object,
            _publisherMock.Object,
            _timeProvider,
            NullLogger<TbMessageHandler>.Instance);
    }

    private static TbConsumerInputDto CreateValidInput(int tileCount = 1) => new()
    {
        RequestId = "req-001",
        TaskId = "task-001",
        MissionMetadata = new MissionMetadataDto
        {
            MissionId = "mission-1",
            TenantId = "tenant-1",
            Overlay = new OverlayDto
            {
                ImageId = "img-001",
                ImageUrl = "/images/test.tiff",
                RuleId = "rule-1",
                AlgorithmNames = [AlgorithmName.FindAir, AlgorithmName.Rpn],
                ResolutionMPerPx = 0.5,
                ImageWidth = 1024,
                ImageHeight = 1024,
                SensorName = "sensor-x",
                SensorType = "EO",
                ImageTime = DateTimeOffset.Parse("2026-07-07T12:00:00Z"),
                RoiFootprint = System.Text.Json.JsonDocument.Parse("{}").RootElement
            }
        },
        ModelMetadata = new ModelMetadataDto
        {
            OverlapHeight = 32,
            OverlapWidth = 32,
            TbCropSizeX = 256,
            TbCropSizeY = 256
        },
        Tiles = Enumerable.Range(0, tileCount)
            .Select(i => new TileBuilderTileOutput
            {
                Roi = [0.0, 0.0, 1.0, 1.0],
                Uri = $"/tiles/tile_{i}.tiff",
                TileIndex = i
            }).ToList()
    };

    private static RabbitMqMessageEnvelope ToEnvelope(object payload) =>
        RabbitMqMessageEnvelope.FromUtf8(JsonSerializer.Serialize(payload));

    private void SetupProjectionMapperPassthrough()
    {
        _projectionMapperMock
            .Setup(m => m.ProcessBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((string _, IReadOnlyList<IReadOnlyList<double>> coords, CancellationToken _) => coords);
    }

    [Fact]
    public async Task HandleAsync_ValidSingleTile_PublishesOneMessageWithHeaders()
    {
        // Arrange
        var input = CreateValidInput(1);
        var envelope = ToEnvelope(input) with
        {
            CorrelationId = "correlation-1",
            Headers = new Dictionary<string, object?>
            {
                ["x-pipeline-start-unix-ms"] = _timeProvider.GetUtcNow().AddSeconds(-1).ToUnixTimeMilliseconds(),
                ["business-header"] = "preserved"
            }
        };
        SetupProjectionMapperPassthrough();

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.True(result.IsSuccess);
        _publisherMock.Verify(
            p => p.PublishToOutputAsync(
                It.Is<RabbitMqMessageEnvelope>(e =>
                    e.Headers != null &&
                    e.Headers.ContainsKey("algorithm_name") &&
                    (string)e.Headers["algorithm_name"]! == "FindAir,Rpn" &&
                    e.Headers.ContainsKey("x-pipeline-start-unix-ms") &&
                    (string)e.Headers["business-header"]! == "preserved" &&
                    e.CorrelationId == "correlation-1"),
                It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task HandleAsync_BatchOfThreeTiles_PublishesThreeMessages()
    {
        // Arrange
        var input = CreateValidInput(3);
        var envelope = ToEnvelope(input);
        SetupProjectionMapperPassthrough();

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.True(result.IsSuccess);
        _projectionMapperMock.Verify(
            m => m.ProcessBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(), It.IsAny<CancellationToken>()),
            Times.Once);
        _publisherMock.Verify(
            p => p.PublishToOutputAsync(It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Exactly(3));
    }

    [Fact]
    public async Task HandleAsync_RoiBoundingBox_SendsSingleCenterPointToProjectionMapper()
    {
        var input = CreateValidInput();
        input.Tiles[0].Roi = [100.0, 200.0, 300.0, 400.0];
        IReadOnlyList<IReadOnlyList<double>>? capturedCoordinates = null;
        _projectionMapperMock
            .Setup(m => m.ProcessBatchAsync(
                "img-001",
                It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<IReadOnlyList<double>>, CancellationToken>(
                (_, coordinates, _) => capturedCoordinates = coordinates)
            .ReturnsAsync([[34.8, 32.1]]);

        var result = await _handler.HandleAsync(ToEnvelope(input));

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedCoordinates);
        var center = Assert.Single(capturedCoordinates);
        Assert.Equal([200.0, 300.0], center);
    }

    [Fact]
    public async Task HandleAsync_MultipleTiles_BatchesOneUnroundedCenterPerTileAndMapsResultsInOrder()
    {
        var input = CreateValidInput(2);
        input.Tiles[0].Roi = [100.0, 200.0, 301.0, 401.0];
        input.Tiles[1].Roi = [10.0, 20.0, 30.0, 60.0];
        IReadOnlyList<IReadOnlyList<double>>? capturedCoordinates = null;
        _projectionMapperMock
            .Setup(m => m.ProcessBatchAsync(
                "img-001",
                It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<IReadOnlyList<double>>, CancellationToken>(
                (_, coordinates, _) => capturedCoordinates = coordinates)
            .ReturnsAsync(
            [
                [34.75, 32.125],
                [35.5, 33.25]
            ]);

        var published = new List<EmbedderInputDto>();
        _publisherMock
            .Setup(p => p.PublishToOutputAsync(
                It.IsAny<RabbitMqMessageEnvelope>(),
                It.IsAny<CancellationToken>()))
            .Callback<RabbitMqMessageEnvelope, CancellationToken>((envelope, _) =>
            {
                published.Add(JsonSerializer.Deserialize<EmbedderInputDto>(
                    envelope.BodyAsUtf8(),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!);
            })
            .Returns(Task.CompletedTask);

        var result = await _handler.HandleAsync(ToEnvelope(input));

        Assert.True(result.IsSuccess);
        Assert.NotNull(capturedCoordinates);
        Assert.Equal(2, capturedCoordinates.Count);
        Assert.All(capturedCoordinates, center => Assert.Equal(2, center.Count));
        Assert.Equal([200.5, 300.5], capturedCoordinates[0]);
        Assert.Equal([20.0, 40.0], capturedCoordinates[1]);

        Assert.Equal(2, published.Count);
        Assert.Equal("0", published[0].EmbedderInput.TileId);
        Assert.Equal(34.75, published[0].EmbedderInput.Lon);
        Assert.Equal(32.125, published[0].EmbedderInput.Lat);
        Assert.Equal("1", published[1].EmbedderInput.TileId);
        Assert.Equal(35.5, published[1].EmbedderInput.Lon);
        Assert.Equal(33.25, published[1].EmbedderInput.Lat);
    }

    [Fact]
    public async Task HandleAsync_PublishesAuthoritativeCorrelationBaggageAndRestoresAmbientState()
    {
        var previous = Baggage.Current;
        try
        {
            Baggage.Current = Baggage.Create(new Dictionary<string, string>
            {
                [TelemetryAttributeNames.PipelineTaskId] = "spoofed-task",
                [TelemetryAttributeNames.PipelineRequestId] = "spoofed-request",
                ["secret"] = "do-not-forward"
            });
            var input = CreateValidInput();
            SetupProjectionMapperPassthrough();
            IReadOnlyDictionary<string, string>? publishedBaggage = null;
            _publisherMock
                .Setup(p => p.PublishToOutputAsync(
                    It.IsAny<RabbitMqMessageEnvelope>(),
                    It.IsAny<CancellationToken>()))
                .Callback(() => publishedBaggage = Baggage.Current.GetBaggage().ToDictionary(
                    static item => item.Key,
                    static item => item.Value,
                    StringComparer.Ordinal))
                .Returns(Task.CompletedTask);

            var result = await _handler.HandleAsync(ToEnvelope(input));

            Assert.True(result.IsSuccess);
            Assert.NotNull(publishedBaggage);
            Assert.Equal("task-001", publishedBaggage[TelemetryAttributeNames.PipelineTaskId]);
            Assert.Equal("req-001", publishedBaggage[TelemetryAttributeNames.PipelineRequestId]);
            Assert.Equal("img-001", publishedBaggage[TelemetryAttributeNames.PipelineImageId]);
            Assert.Equal("rule-1", publishedBaggage[TelemetryAttributeNames.PipelineRuleId]);
            Assert.Equal("tenant-1", publishedBaggage[TelemetryAttributeNames.PipelineTenantId]);
            Assert.Equal("FindAir,Rpn", publishedBaggage[TelemetryAttributeNames.PipelineAlgorithmName]);
            Assert.DoesNotContain("secret", publishedBaggage.Keys);
            Assert.Equal("spoofed-task", Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineTaskId));
            Assert.Equal("do-not-forward", Baggage.Current.GetBaggage("secret"));
        }
        finally
        {
            Baggage.Current = previous;
        }
    }

    [Fact]
    public async Task HandleAsync_CreatesAggregateValidationProjectionAndBuildSpans()
    {
        var input = CreateValidInput(2);
        SetupProjectionMapperPassthrough();
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.TbConsumer);
        using var handlerActivity = new Activity("rabbitmq handler").Start();
        handlerActivity.IsAllDataRequested = true;

        var result = await _handler.HandleAsync(ToEnvelope(input));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["tb_consumer.validate", "tb_consumer.projection", "tb_consumer.build"],
            activities.Activities.Select(activity => activity.DisplayName).ToArray());
        Assert.All(activities.Activities, activity => Assert.Equal(ActivityKind.Internal, activity.Kind));
        Assert.All(activities.Activities, activity => Assert.Equal(ActivityStatusCode.Ok, activity.Status));
        Assert.Equal("task-001", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineTaskId));
        Assert.Equal("req-001", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineRequestId));
        Assert.Equal("img-001", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineImageId));
        Assert.Equal("rule-1", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineRuleId));
        Assert.Equal("tenant-1", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineTenantId));
        Assert.Equal("FindAir,Rpn", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineAlgorithmName));
    }

    [Fact]
    public async Task HandleAsync_PublishedPayload_ContainsCorrectEmbedderInputDto()
    {
        // Arrange
        var input = CreateValidInput(1);
        var envelope = ToEnvelope(input);
        SetupProjectionMapperPassthrough();

        RabbitMqMessageEnvelope? captured = null;
        _publisherMock
            .Setup(p => p.PublishToOutputAsync(It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback<RabbitMqMessageEnvelope, CancellationToken>((env, _) => captured = env)
            .Returns(Task.CompletedTask);

        // Act
        await _handler.HandleAsync(envelope);

        // Assert — deserialize as EmbedderInputDto envelope
        Assert.NotNull(captured);
        var dto = JsonSerializer.Deserialize<EmbedderInputDto>(
            captured!.BodyAsUtf8(),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(dto);

        // Verify EmbedderInput sub-object
        var embedder = dto!.EmbedderInput;
        Assert.Equal("0", embedder.TileId);
        Assert.Equal("req-001", embedder.Gid);
        Assert.Equal("/tiles/tile_0.tiff", embedder.ImagePath);
        Assert.Equal("sensor-x", embedder.Sensor);
        Assert.Equal(0.5, embedder.Resolution);
        Assert.Equal("tenant-1", embedder.TenantId);
        Assert.Equal(0.5, embedder.Lon);
        Assert.Equal(0.5, embedder.Lat);
        Assert.NotNull(embedder.ImagingTime);
        Assert.Equal(["FindAir", "Rpn"], embedder.Algorithms);
        Assert.NotNull(embedder.TileCoordinates);
        Assert.NotEmpty(embedder.TileCoordinates.Coordinates);

        // Verify top-level metadata fields
        Assert.Equal("req-001", dto!.RequestId);
        Assert.Equal("task-001", dto.TaskId);
        Assert.Equal("tenant-1", dto.MissionMetadata.TenantId);
        Assert.Equal("mission-1", dto.MissionMetadata.MissionId);
    }

    [Fact]
    public async Task HandleAsync_MissingTenantId_ReturnsFailure()
    {
        // Arrange
        var input = CreateValidInput();
        input.MissionMetadata.TenantId = "";
        var envelope = ToEnvelope(input);

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("Validation failed", result.Error);
    }

    [Fact]
    public async Task HandleAsync_EmptyTiles_ReturnsFailure()
    {
        // Arrange
        var input = CreateValidInput();
        input.Tiles = [];
        var envelope = ToEnvelope(input);

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("Validation failed", result.Error);
    }

    [Fact]
    public async Task HandleAsync_ProjectionMapperThrows_PropagatesException()
    {
        // Arrange
        var input = CreateValidInput();
        var envelope = ToEnvelope(input);
        _projectionMapperMock
            .Setup(m => m.ProcessBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Mapper crashed"));

        // Act & Assert — exception propagates to RabbitMqConsumer for DLQ routing
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _handler.HandleAsync(envelope));
        Assert.Contains("Mapper crashed", ex.Message);
        _publisherMock.Verify(
            p => p.PublishToOutputAsync(It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task HandleAsync_ProjectionCancellation_MarksProjectionSpanCancelled()
    {
        var input = CreateValidInput();
        _projectionMapperMock
            .Setup(m => m.ProcessBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<IReadOnlyList<double>>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Projection cancelled."));
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.TbConsumer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.HandleAsync(ToEnvelope(input)));

        var activity = Assert.Single(
            activities.Activities,
            activity => activity.DisplayName == "tb_consumer.projection");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("cancelled", activity.GetTagItem(TelemetryAttributeNames.ErrorCategory));
    }

    [Fact]
    public async Task HandleAsync_PublishCancellation_MarksBuildSpanCancelled()
    {
        var input = CreateValidInput();
        SetupProjectionMapperPassthrough();
        _publisherMock
            .Setup(p => p.PublishToOutputAsync(It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException("Publish cancelled."));
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.TbConsumer);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _handler.HandleAsync(ToEnvelope(input)));

        var activity = Assert.Single(
            activities.Activities,
            activity => activity.DisplayName == "tb_consumer.build");
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal("cancelled", activity.GetTagItem(TelemetryAttributeNames.ErrorCategory));
    }

    [Fact]
    public async Task HandleAsync_PublisherThrowsMidBatch_PropagatesException()
    {
        // Arrange — 3 tiles, publisher throws on 2nd
        var input = CreateValidInput(3);
        var envelope = ToEnvelope(input);
        SetupProjectionMapperPassthrough();

        var callCount = 0;
        _publisherMock
            .Setup(p => p.PublishToOutputAsync(It.IsAny<RabbitMqMessageEnvelope>(), It.IsAny<CancellationToken>()))
            .Callback(() => callCount++)
            .Returns<RabbitMqMessageEnvelope, CancellationToken>((_, _) =>
                callCount == 2
                    ? throw new Exception("Publish failed on tile 2")
                    : Task.CompletedTask);

        // Act & Assert — exception propagates to RabbitMqConsumer for DLQ routing
        var ex = await Assert.ThrowsAsync<Exception>(() => _handler.HandleAsync(envelope));
        Assert.Contains("Publish failed on tile 2", ex.Message);
    }

    [Fact]
    public async Task HandleAsync_InvalidJson_ReturnsFailure()
    {
        // Arrange
        var envelope = RabbitMqMessageEnvelope.FromUtf8("not valid json {{{}");

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("Json deserialization failed", result.Error);
    }

    [Fact]
    public async Task HandleAsync_NullDeserialization_ReturnsFailure()
    {
        // Arrange
        var envelope = RabbitMqMessageEnvelope.FromUtf8("null");

        // Act
        var result = await _handler.HandleAsync(envelope);

        // Assert
        Assert.False(result.IsSuccess);
        Assert.Contains("Deserialization produced null", result.Error);
    }

    [Fact]
    public async Task HandleAsync_InvalidJson_DoesNotRecordFanOut()
    {
        using var fanOut = new FanOutMeasurementCollector();

        var result = await _handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8("not valid json {{{}"));

        Assert.False(result.IsSuccess);
        Assert.Empty(fanOut.Measurements);
    }

    [Fact]
    public async Task HandleAsync_ValidationFailure_DoesNotRecordFanOut()
    {
        var input = CreateValidInput();
        input.MissionMetadata.TenantId = "";
        using var fanOut = new FanOutMeasurementCollector();

        var result = await _handler.HandleAsync(ToEnvelope(input));

        Assert.False(result.IsSuccess);
        Assert.Empty(fanOut.Measurements);
    }

    [Fact]
    public async Task HandleAsync_Success_RecordsPublishedFanOut()
    {
        SetupProjectionMapperPassthrough();
        using var fanOut = new FanOutMeasurementCollector();

        var result = await _handler.HandleAsync(ToEnvelope(CreateValidInput(tileCount: 3)));

        Assert.True(result.IsSuccess);
        Assert.Equal(3, Assert.Single(fanOut.Measurements));
    }

    private sealed class FanOutMeasurementCollector : IDisposable
    {
        private readonly ConcurrentQueue<long> _measurements = new();
        private readonly MeterListener _listener;

        public FanOutMeasurementCollector()
        {
            _listener = new MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Name == TelemetryMetricNames.PipelineFanOut)
                    {
                        listener.EnableMeasurementEvents(instrument);
                    }
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == TelemetryAttributeNames.PipelineStage
                        && string.Equals(tag.Value as string, "tb_consumer", StringComparison.Ordinal))
                    {
                        _measurements.Enqueue(measurement);
                        break;
                    }
                }
            });
            _listener.Start();
        }

        public IReadOnlyCollection<long> Measurements => _measurements.ToArray();

        public void Dispose() => _listener.Dispose();
    }
}
