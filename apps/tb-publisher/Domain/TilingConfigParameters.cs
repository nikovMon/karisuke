using System.Text.Json.Serialization;

namespace ImagingPipeline.TbPublisher.Domain;

public sealed class TilingConfigParameters
{
    [JsonPropertyName("tiling_size_width")]
    public int TileSizeWidth { get; set; }

    [JsonPropertyName("tiling_size_height")]
    public int TileSizeHeight { get; set; }

    [JsonPropertyName("tile_overlap_width")]
    public int TileOverlapWidth { get; set; }

    [JsonPropertyName("tile_overlap_height")]
    public int TileOverlapHeight { get; set; }

    public bool IsValid(out string error)
    {
        if (TileSizeWidth <= 0 || TileSizeHeight <= 0)
        {
            error = "tiling_size_width and tiling_size_height must be greater than 0.";
            return false;
        }

        if (TileOverlapWidth < 0 || TileOverlapHeight < 0)
        {
            error = "tile_overlap_width and tile_overlap_height cannot be negative.";
            return false;
        }

        if (TileOverlapWidth >= TileSizeWidth || TileOverlapHeight >= TileSizeHeight)
        {
            error = "tile overlap must be smaller than the corresponding tile size.";
            return false;
        }

        error = string.Empty;
        return true;
    }
}
