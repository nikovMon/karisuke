using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Dtos.Messages;

public sealed class GatewayMatchedOutputDto
{
    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("algorithmName")]
    public string AlgorithmName { get; set; } = string.Empty;

    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("tenantId")]
    public string TenantId { get; set; } = string.Empty;

    [JsonPropertyName("tilingConfigs")]
    public List<TilingConfig> TilingConfigs { get; set; } = [];

    [JsonPropertyName("matchedAt")]
    public DateTimeOffset MatchedAt { get; set; }
}
