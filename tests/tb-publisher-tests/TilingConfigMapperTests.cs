using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.TbPublisher.Application;
using ImagingPipeline.TbPublisher.Dtos.Inbound;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TilingConfigMapperTests
{
    private readonly TilingConfigMapper _mapper = new();

    private const string FocusedPxWkt = "POLYGON((0 0, 1 0, 1 1, 0 1, 0 0))";
    private const string MissionId = "mission-1";

    [Fact]
    public void MapReturnsOneMessagePerTilingConfig()
    {
        var message = new TbMessageDto
        {
            RuleId = "rule-1",
            AlgorithmName = AlgorithmName.FindAir,
            TenantId = "tenant-1",
            ImageId = "image-1",
            RoiFootprint = JsonDocument.Parse("""{ "type": "Point", "coordinates": [35.98, 34.15] }""").RootElement,
            TilingConfigs =
            [
                new TilingConfig { TileSizeWidth = 110, TileSizeHeight = 110, TileOverlapWidth = 10, TileOverlapHeight = 10 },
                new TilingConfig { TileSizeWidth = 250, TileSizeHeight = 250, TileOverlapWidth = 10, TileOverlapHeight = 10 }
            ]
        };

        var result = _mapper.Map(message, FocusedPxWkt, MissionId);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Messages);
        Assert.Equal(2, result.Messages!.Count);

        foreach (var outputMessage in result.Messages)
        {
            Assert.Equal(FocusedPxWkt, outputMessage.FocusedPxWkt);
            Assert.Equal(MissionId, outputMessage.MissionMetadata.MissionId);
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
        Assert.Equal(250, result.Messages[1].ModelMetadata.TbCropSizeX);
    }

    [Fact]
    public void MapReturnsFailureWhenTilingConfigsIsEmpty()
    {
        var message = new TbMessageDto
        {
            RuleId = "rule-1",
            TenantId = "tenant-1",
            ImageId = "image-1",
            TilingConfigs = []
        };

        var result = _mapper.Map(message, FocusedPxWkt, MissionId);

        Assert.False(result.IsSuccess);
        Assert.Null(result.Messages);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MapReturnsFailureWhenAnyTilingConfigParametersAreInvalid()
    {
        var message = new TbMessageDto
        {
            RuleId = "rule-1",
            TenantId = "tenant-1",
            ImageId = "image-1",
            TilingConfigs =
            [
                new TilingConfig { TileSizeWidth = 0, TileSizeHeight = 512 }
            ]
        };

        var result = _mapper.Map(message, FocusedPxWkt, MissionId);

        Assert.False(result.IsSuccess);
        Assert.Contains("tileSizeWidth", result.Error);
    }
}
