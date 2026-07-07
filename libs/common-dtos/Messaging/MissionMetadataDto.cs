using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Messaging;

public sealed class MissionMetadataDto
{
    [JsonPropertyName("missionId")]
    public string MissionId { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("overlay")]
    public OverlayDto Overlay { get; set; } = new();
}

public sealed class OverlayDto
{
    [JsonPropertyName("image_id")]
    public string ImageId { get; set; } = string.Empty;

    [JsonPropertyName("image_url")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("resolution_m_per_px")]
    public double ResolutionMPerPx { get; set; }

    [JsonPropertyName("algorithm_name")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlgorithmName AlgorithmName { get; set; }

    [JsonPropertyName("image_width")]
    public int ImageWidth { get; set; }

    [JsonPropertyName("image_height")]
    public int ImageHeight { get; set; }

    [JsonPropertyName("roifootprint")]
    public JsonElement RoiFootprint { get; set; }

    [JsonPropertyName("image_time")]
    public DateTimeOffset? ImageTime { get; set; }

    [JsonPropertyName("sensor_name")]
    public string? SensorName { get; set; }

    [JsonPropertyName("sensor_type")]
    public string? SensorType { get; set; }
}
