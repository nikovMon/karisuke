using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

public sealed class TilingConfig
{
    [JsonPropertyName("tileSizeWidth")]
    public int TileSizeWidth { get; set; }

    [JsonPropertyName("tileSizeHeight")]
    public int TileSizeHeight { get; set; }

    [JsonPropertyName("tileOverlapWidth")]
    public int TileOverlapWidth { get; set; }

    [JsonPropertyName("tileOverlapHeight")]
    public int TileOverlapHeight { get; set; }
}
