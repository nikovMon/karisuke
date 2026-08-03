using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class EmbedderInputPayload
{
    [JsonPropertyName("tile_id")]
    public string TileId { get; set; } = string.Empty;

    [JsonPropertyName("gid")]
    public string? Gid { get; set; }

    [JsonPropertyName("image_path")]
    public string ImagePath { get; set; } = string.Empty;

    [JsonPropertyName("sensor")]
    public string? Sensor { get; set; }

    [JsonPropertyName("imaging_time")]
    public DateTimeOffset? ImagingTime { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    [JsonPropertyName("resolution")]
    public double? Resolution { get; set; }

    [JsonPropertyName("tiles_size_meters")]
    public double? TilesSizeMeters { get; set; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("request_time")]
    public DateTimeOffset? RequestTime { get; set; }

    [JsonPropertyName("tile_coordinates")]
    public PolygonDto TileCoordinates { get; set; } = new();

    [JsonPropertyName("algorithms")]
    public List<string> Algorithms { get; set; } = new();

    [JsonPropertyName("pixel_roi")]
    public double[] PixelRoi { get; set; } = [];
}
