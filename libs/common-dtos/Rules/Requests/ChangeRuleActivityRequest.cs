using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class ChangeRuleActivityRequest
{
    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }
}
