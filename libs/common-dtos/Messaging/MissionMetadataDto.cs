using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class MissionMetadataDto
{
    [JsonPropertyName("missionId")] public string MissionId { get; set; } = string.Empty;
    [JsonPropertyName("tenantId")] public string TenantId { get; set; } = string.Empty;
    [JsonPropertyName("overlay")] public OverlayDto Overlay { get; set; } = new();
}
