using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Contracts.Requests;

public sealed class ChangeRuleActivityRequest
{
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
