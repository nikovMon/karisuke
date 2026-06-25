using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Rules.Contracts.Models;

namespace ImagingPipeline.Rules.Contracts.Requests;

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

    [JsonPropertyName("tenants")]
    public List<TenantConfigDto>? Tenants { get; set; }

    [JsonPropertyName("minResulution")]
    public double? MinResolution { get; set; }

    [JsonPropertyName("maxResulution")]
    public double? MaxResolution { get; set; }

    [JsonPropertyName("area")]
    public string? Area { get; set; }

    [JsonPropertyName("wkt")]
    public string? Wkt { get; set; }

    [JsonPropertyName("geoJson")]
    public JsonElement? GeoJson { get; set; }

    [JsonPropertyName("maxLookBackDay")]
    public int? MaxLookBackDay { get; set; }

    public bool HasField(string jsonPropertyName) => ProvidedFields.Contains(jsonPropertyName);
}
