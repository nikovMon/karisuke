using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class TileBuilderTileOutput
{
    [JsonPropertyName("roi")]
    public double[] Roi { get; set; } = [];

    [JsonPropertyName("uri")]
    public string Uri { get; set; } = string.Empty;

    [JsonPropertyName("tile_index")]
    public int TileIndex { get; set; }
}
