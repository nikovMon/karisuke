using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

public sealed class TenantInfo
{
    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("tilingConfigs")]
    public List<TilingConfig> TilingConfigs { get; set; } = [];
}
