using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

/// <summary>
/// A new v2 rule. The server assigns the ID and timestamps and derives <c>match.location</c>
/// from <c>match.locationWkt</c>.
/// </summary>
public sealed class CreatePipelineRuleRequest
{
    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("matchAll")]
    public bool MatchAll { get; set; }

    [JsonPropertyName("match")]
    public RuleMatchConditions? Match { get; set; }

    [JsonPropertyName("runParams")]
    public List<JsonElement>? RunParams { get; set; }

    public PipelineRuleDocument ToDocument() => new()
    {
        RuleName = RuleName,
        Description = Description,
        IsActive = IsActive,
        MatchAll = MatchAll,
        Match = Match,
        RunParams = RunParams
    };
}
