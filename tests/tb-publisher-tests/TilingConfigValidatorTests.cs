using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TilingConfigValidatorTests
{
    [Fact]
    public void IsValidReturnsTrueForPositiveSizesAndSmallerOverlap()
    {
        var config = new TilingConfig
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = 32,
            TileOverlapHeight = 32
        };

        Assert.True(TilingConfigValidator.IsValid(config, out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(512, 0)]
    [InlineData(-1, 512)]
    public void IsValidReturnsFalseWhenTileSizeIsNotPositive(int width, int height)
    {
        var config = new TilingConfig { TileSizeWidth = width, TileSizeHeight = height };

        Assert.False(TilingConfigValidator.IsValid(config, out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void IsValidReturnsFalseWhenOverlapIsNegative(int overlapWidth, int overlapHeight)
    {
        var config = new TilingConfig
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = overlapWidth,
            TileOverlapHeight = overlapHeight
        };

        Assert.False(TilingConfigValidator.IsValid(config, out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValidReturnsFalseWhenOverlapIsNotSmallerThanTileSize()
    {
        var config = new TilingConfig
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = 512,
            TileOverlapHeight = 0
        };

        Assert.False(TilingConfigValidator.IsValid(config, out var error));
        Assert.NotEmpty(error);
    }
}
