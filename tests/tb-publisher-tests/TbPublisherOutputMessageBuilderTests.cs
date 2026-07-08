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

        var messages = _builder.Map(message, FocusedPxWkt);

        Assert.Equal(2, messages.Count);

        var expectedMissionId = DeterministicIdGenerator.CreateMissionId(message.TaskId);

        foreach (var outputMessage in messages)
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

        Assert.NotEqual(messages[0].RequestId, messages[1].RequestId);
        Assert.Equal(messages[0].TaskId, messages[1].TaskId);

        Assert.Equal(110, messages[0].ModelMetadata.TbCropSizeX);
        Assert.Equal(110, messages[0].ModelMetadata.TbCropSizeY);
        Assert.Equal(10, messages[0].ModelMetadata.OverlapWidth);
        Assert.Equal(10, messages[0].ModelMetadata.OverlapHeight);

        Assert.Equal(250, messages[1].ModelMetadata.TbCropSizeX);
        Assert.Equal(180, messages[1].ModelMetadata.TbCropSizeY);
        Assert.Equal(20, messages[1].ModelMetadata.OverlapWidth);
        Assert.Equal(15, messages[1].ModelMetadata.OverlapHeight);
    }

    [Fact]
    public void MapDerivesTheSameIdentifiersWhenCalledTwiceForTheSameMessageId()
    {
        var first = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);
        var second = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);

        Assert.Equal(first[0].MissionMetadata.MissionId, second[0].MissionMetadata.MissionId);
        Assert.Equal(first[0].RequestId, second[0].RequestId);
        Assert.Equal(first[0].TaskId, second[0].TaskId);
    }

    [Fact]
    public void MapDerivesDifferentIdentifiersForDifferentMessageIds()
    {
        var first = _builder.Map(CreateMessage("msg-1"), FocusedPxWkt);
        var second = _builder.Map(CreateMessage("msg-2"), FocusedPxWkt);

        Assert.NotEqual(first[0].MissionMetadata.MissionId, second[0].MissionMetadata.MissionId);
    }

    private static GatewayOutputMessageDto CreateMessage(string id) => new()
    {
        TaskId = id,
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
