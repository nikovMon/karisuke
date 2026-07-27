using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;
using Microsoft.Extensions.Time.Testing;
using Moq;

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
            _timeProvider);
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
        var envelope = ToEnvelope(input);
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
                    (string)e.Headers["algorithm_name"]! == "FindAir,Rpn"),
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
        Assert.Equal(0.0, embedder.Lon);
        Assert.Equal(0.0, embedder.Lat);
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
}
