using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class CreateRuleRequest : IValidatableObject
{
    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("algorithmName")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public required AlgorithmName AlgorithmName { get; set; }

    [JsonPropertyName("sensors")]
    public Dictionary<string, List<string>> Sensors { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("tenantsInfo")]
    public List<TenantInfo> TenantsInfo { get; set; } = [];

    [JsonPropertyName("minimumResolution")]
    public double MinimumResolution { get; set; }

    [JsonPropertyName("maximumResolution")]
    public double MaximumResolution { get; set; }

    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("locationWkt")]
    [Required]
    public string LocationWkt { get; set; } = string.Empty;

    [JsonPropertyName("locationGeoJson")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LocationGeoJson { get; set; }

    [JsonPropertyName("isPhotoOld")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPhotoOld { get; set; }

    public RuleDto ToRuleDto() =>
        new()
        {
            RuleName = RuleName,
            Description = Description,
            AlgorithmName = AlgorithmName,
            Sensors = Sensors,
            IsActive = IsActive,
            TenantsInfo = TenantsInfo,
            MinimumResolution = MinimumResolution,
            MaximumResolution = MaximumResolution,
            Area = Area,
            LocationWkt = LocationWkt,
            LocationGeoJson = LocationGeoJson,
            IsPhotoOld = IsPhotoOld
        };

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext) =>
        ToRuleDto().Validate(validationContext);
}
