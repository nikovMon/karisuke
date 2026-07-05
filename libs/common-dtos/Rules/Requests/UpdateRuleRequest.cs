using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

[JsonConverter(typeof(UpdateRuleRequestJsonConverter))]
public sealed class UpdateRuleRequest
{
    public ISet<string> ProvidedFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("ruleName")]
    public string? RuleName { get; set; }

    [JsonPropertyName("description")]
    public string? Description { get; set; }

    [JsonPropertyName("algorithmName")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlgorithmName? AlgorithmName { get; set; }

    [JsonPropertyName("sensors")]
    public Dictionary<string, List<string>>? Sensors { get; set; }

    [JsonPropertyName("isActive")]
    public bool? IsActive { get; set; }

    [JsonPropertyName("tenantsInfo")]
    public List<TenantInfo>? TenantsInfo { get; set; }

    [JsonPropertyName("minimumResolution")]
    public double? MinimumResolution { get; set; }

    [JsonPropertyName("maximumResolution")]
    public double? MaximumResolution { get; set; }

    [JsonPropertyName("area")]
    public string? Area { get; set; }

    [JsonPropertyName("locationWkt")]
    public string? LocationWkt { get; set; }

    [JsonPropertyName("locationGeoJson")]
    public JsonElement? LocationGeoJson { get; set; }

    [JsonPropertyName("isPhotoOld")]
    public bool? IsPhotoOld { get; set; }

    public bool HasField(string jsonPropertyName) => ProvidedFields.Contains(jsonPropertyName);
}
