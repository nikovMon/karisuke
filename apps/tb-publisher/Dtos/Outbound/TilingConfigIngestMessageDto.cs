using System.Text.Json.Serialization;
using ImagingPipeline.TbPublisher.Domain;

namespace ImagingPipeline.TbPublisher.Dtos.Outbound;

public sealed class TilingConfigIngestMessageDto
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("imageId")]
    public string ImageId { get; set; } = string.Empty;

    [JsonPropertyName("tilingConfig")]
    public TilingConfigParameters TilingConfig { get; set; } = new();

    [JsonPropertyName("coordinates")]
    public IReadOnlyList<IReadOnlyList<double>> Coordinates { get; set; } = [];

    [JsonPropertyName("photoTime")]
    public DateTimeOffset? PhotoTime { get; set; }

    [JsonPropertyName("sensorType")]
    public string? SensorType { get; set; }

    [JsonPropertyName("processedAt")]
    public DateTimeOffset ProcessedAt { get; set; }
}
