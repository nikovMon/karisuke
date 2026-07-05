using System.Text.Json;
using ImagingPipeline.TbPublisher.Application;
using ImagingPipeline.TbPublisher.Domain;
using ImagingPipeline.TbPublisher.Dtos.Inbound;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TilingConfigMapperTests
{
    private readonly TilingConfigMapper _mapper = new();

    private static readonly IReadOnlyList<IReadOnlyList<double>> Coordinates = [[0, 0], [1, 1]];

    [Fact]
    public void MapReturnsOneMessagePerTilingConfig()
    {
        var message = new TbMessageDto
        {
            RuleId = "rule-1",
            AlgorithmName = AlgorithmName.Flare,
            TenantId = "tenant-1",
            ImageId = "image-1",
            RoiFootprint = JsonDocument.Parse("[[35.98, 34.15]]").RootElement,
            TilingConfigs =
            [
                new TilingConfigParameters { TileSizeWidth = 110, TileSizeHeight = 110, TileOverlapWidth = 10, TileOverlapHeight = 10 },
                new TilingConfigParameters { TileSizeWidth = 250, TileSizeHeight = 250, TileOverlapWidth = 10, TileOverlapHeight = 10 }
            ]
        };

        var result = _mapper.Map(message, Coordinates);

        Assert.True(result.IsSuccess);
        Assert.NotNull(result.Messages);
        Assert.Equal(2, result.Messages!.Count);

        foreach (var ingestMessage in result.Messages)
        {
            Assert.Equal("rule-1", ingestMessage.RuleId);
            Assert.Equal("tenant-1", ingestMessage.TenantId);
            Assert.Equal("image-1", ingestMessage.ImageId);
            Assert.Same(Coordinates, ingestMessage.Coordinates);
            Assert.True(ingestMessage.ProcessedAt > DateTimeOffset.MinValue);
        }

        Assert.Equal(110, result.Messages[0].TilingConfig.TileSizeWidth);
        Assert.Equal(250, result.Messages[1].TilingConfig.TileSizeWidth);
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

        var result = _mapper.Map(message, Coordinates);

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
                new TilingConfigParameters { TileSizeWidth = 0, TileSizeHeight = 512 }
            ]
        };

        var result = _mapper.Map(message, Coordinates);

        Assert.False(result.IsSuccess);
        Assert.Contains("tiling_size_width", result.Error);
    }
}
