using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.TbPublisher.Application;
using ImagingPipeline.TbPublisher.Domain;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TbPublisherOutputMessageBuilderTests
{
    private readonly TbPublisherOutputMessageBuilder _builder = new();

    private const string FocusedPxWkt = "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))";

    [Fact]
    public void MapReturnsOneMessagePerTilingConfig()
    {
        var message = CreateMessage("msg-1");

        var result = _builder.Map(message, FocusedPxWkt);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Messages);
        Assert.Equal(2, result.Messages!.Count);

        var expectedMissionId = DeterministicIdGenerator.CreateMissionId(message.Id);

        foreach (var outputMessage in result.Messages)
        {
            Assert.Equal(FocusedPxWkt, outputMessage.FocusedPxWkt);
            Assert.Equal(expectedMissionId, outputMessage.MissionMetadata.MissionId);
            Assert.Equal("tenant-1", outputMessage.MissionMetadata.TenantId);
            Assert.Equal("rule-1", outputMessage.MissionMetadata.Overlay.RuleId);
            Assert.Equal("image-1", outputMessage.MissionMetadata.Overlay.ImageId);
            Assert.Equal("image-1", outputMessage.FrameMetadata.General.Id);
            Assert.NotEmpty(outputMessage.RequestId);
            Assert.NotEmpty(outputMessage.TaskId);
        }

        Assert.NotEqual(result.Messages[0].RequestId, result.Messages[1].RequestId);
        Assert.NotEqual(result.Messages[0].TaskId, result.Messages[1].TaskId);

        Assert.Equal(110, result.Messages[0].ModelMetadata.TbCropSizeX);
        Assert.Equal(110, result.Messages[0].ModelMetadata.TbCropSizeY);
        Assert.Equal(10, result.Messages[0].ModelMetadata.OverlapWidth);
        Assert.Equal(10, result.Messages[0].ModelMetadata.OverlapHeight);

        Assert.Equal(250, result.Messages[1].ModelMetadata.TbCropSizeX);
        Assert.Equal(180, result.Messages[1].ModelMetadata.TbCropSizeY);
        Assert.Equal(20, result.Messages[1].ModelMetadata.OverlapWidth);
        Assert.Equal(15, result.Messages[1].ModelMetadata.OverlapHeight);
    }

    [Fact]
    public void MapDerivesTheSameIdentifiersWhenCalledTwiceForTheSameMessageId()
    {
        var first = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);
        var second = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);

        Assert.Equal(first.Messages![0].MissionMetadata.MissionId, second.Messages![0].MissionMetadata.MissionId);
        Assert.Equal(first.Messages[0].RequestId, second.Messages[0].RequestId);
        Assert.Equal(first.Messages[0].TaskId, second.Messages[0].TaskId);
    }

    [Fact]
    public void MapDerivesDifferentIdentifiersForDifferentMessageIds()
    {
        var first = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);
        var second = _builder.Map(CreateMessage("msg-2"), FocusedPxWkt);

        Assert.NotEqual(first.Messages![0].MissionMetadata.MissionId, second.Messages![0].MissionMetadata.MissionId);
    }

    private static GatewayOutputMessageDto CreateMessage(string id) => new()
    {
        Id = id,
        RuleId = "rule-1",
        AlgorithmName = AlgorithmName.FindAir,
        TenantId = "tenant-1",
        ImageId = "image-1",
        RoiFootprint = JsonDocument.Parse("""{ "type": "Point", "coordinates": [35.98, 34.15] }""").RootElement,
        TilingConfigs =
        [
            new TilingConfig { TileSizeWidth = 110, TileSizeHeight = 110, TileOverlapWidth = 10, TileOverlapHeight = 10 },
            new TilingConfig { TileSizeWidth = 250, TileSizeHeight = 180, TileOverlapWidth = 20, TileOverlapHeight = 15 }
        ]
    };
}
