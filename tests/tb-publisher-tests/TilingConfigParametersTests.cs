using ImagingPipeline.TbPublisher.Domain;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class TilingConfigParametersTests
{
    [Fact]
    public void IsValidReturnsTrueForPositiveSizesAndSmallerOverlap()
    {
        var parameters = new TilingConfigParameters
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = 32,
            TileOverlapHeight = 32
        };

        Assert.True(parameters.IsValid(out var error));
        Assert.Empty(error);
    }

    [Theory]
    [InlineData(0, 512)]
    [InlineData(512, 0)]
    [InlineData(-1, 512)]
    public void IsValidReturnsFalseWhenTileSizeIsNotPositive(int width, int height)
    {
        var parameters = new TilingConfigParameters { TileSizeWidth = width, TileSizeHeight = height };

        Assert.False(parameters.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void IsValidReturnsFalseWhenOverlapIsNegative(int overlapWidth, int overlapHeight)
    {
        var parameters = new TilingConfigParameters
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = overlapWidth,
            TileOverlapHeight = overlapHeight
        };

        Assert.False(parameters.IsValid(out var error));
        Assert.NotEmpty(error);
    }

    [Fact]
    public void IsValidReturnsFalseWhenOverlapIsNotSmallerThanTileSize()
    {
        var parameters = new TilingConfigParameters
        {
            TileSizeWidth = 512,
            TileSizeHeight = 512,
            TileOverlapWidth = 512,
            TileOverlapHeight = 0
        };

        Assert.False(parameters.IsValid(out var error));
        Assert.NotEmpty(error);
    }
}
