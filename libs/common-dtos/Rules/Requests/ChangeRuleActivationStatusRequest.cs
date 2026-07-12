using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class ChangeRuleActivationStatusRequest
{
    // Required at the API boundary so invalid requests are rejected before the service is called.
    [Required]
    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }
}
