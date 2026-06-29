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

    [JsonPropertyName("tenantName")]
    public string TenantName { get; set; } = string.Empty;

    [JsonPropertyName("tilingConfig")]
    public TilingConfigDto TilingConfig { get; set; } = new();

    [JsonPropertyName("matchedAt")]
    public DateTimeOffset MatchedAt { get; set; }
}
