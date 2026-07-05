using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class ChangeRuleActivityRequest
{
    [Required]
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}
