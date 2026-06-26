using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

public sealed class RuleConfigDto : IValidatableObject
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("algorithmName")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlgorithmName? AlgorithmName { get; set; }

    [JsonPropertyName("sensors")]
    public Dictionary<string, List<string>> Sensors { get; set; } = new(StringComparer.Ordinal);

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; }

    [JsonPropertyName("tenants")]
    public List<TenantConfigDto> Tenants { get; set; } = [];

    [JsonPropertyName("minResolution")]
    public double MinResolution { get; set; }

    [JsonPropertyName("maxResolution")]
    public double MaxResolution { get; set; }

    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    [JsonPropertyName("wkt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Wkt { get; set; }

    [JsonPropertyName("geoJson")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? GeoJson { get; set; }

    [JsonPropertyName("maxLookBackDay")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxLookBackDay { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("modifiedAt")]
    public DateTimeOffset ModifiedAt { get; set; } = DateTimeOffset.UtcNow;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(Id))
        {
            yield return new ValidationResult("_id cannot be empty", [nameof(Id)]);
        }

        if (string.IsNullOrWhiteSpace(RuleName))
        {
            yield return new ValidationResult("ruleName cannot be empty", [nameof(RuleName)]);
        }

        if (AlgorithmName is null)
        {
            yield return new ValidationResult("algorithmName is required", [nameof(AlgorithmName)]);
        }

        if (MinResolution <= 0)
        {
            yield return new ValidationResult("minResolution must be greater than 0", [nameof(MinResolution)]);
        }

        var hasWkt = !string.IsNullOrWhiteSpace(Wkt);
        var hasGeoJson = GeoJson.HasValue &&
            GeoJson.Value.ValueKind != JsonValueKind.Null &&
            GeoJson.Value.ValueKind != JsonValueKind.Undefined;

        if (!hasWkt && !hasGeoJson)
        {
            yield return new ValidationResult(
                "RuleConfig must contain wkt, geoJson, or both",
                [nameof(Wkt), nameof(GeoJson)]);
        }
    }
}

public sealed class TenantConfigDto
{
    [JsonPropertyName("tenantName")]
    public string TenantName { get; set; } = string.Empty;

    [JsonPropertyName("tilingConfig")]
    public TilingConfigDto TilingConfig { get; set; } = new();
}

public sealed class TilingConfigDto
{
    [JsonPropertyName("width")]
    public int Width { get; set; }

    [JsonPropertyName("length")]
    public int Length { get; set; }
}

public enum AlgorithmName
{
    Finder,
    rpn
}
