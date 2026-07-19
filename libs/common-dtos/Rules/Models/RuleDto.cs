using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

public sealed class RuleDto : IValidatableObject
{
    [JsonPropertyName("_id")]
    [DataMember(Name = "_id")]
    public string Id { get; set; } = string.Empty;

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
    public string LocationWkt { get; set; } = string.Empty;

    [JsonPropertyName("locationGeoJson")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? LocationGeoJson { get; set; }

    [JsonPropertyName("isPhotoOld")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsPhotoOld { get; set; }

    [JsonPropertyName("creationTime")]
    public DateTimeOffset CreationTime { get; set; }

    [JsonPropertyName("updateTime")]
    public DateTimeOffset UpdateTime { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(RuleName))
        {
            yield return new ValidationResult("ruleName cannot be empty", [nameof(RuleName)]);
        }

        if (MinimumResolution <= 0)
        {
            yield return new ValidationResult(
                "minimumResolution must be greater than 0",
                [nameof(MinimumResolution)]);
        }

        if (MaximumResolution <= 0)
        {
            yield return new ValidationResult(
                "maximumResolution must be greater than 0",
                [nameof(MaximumResolution)]);
        }

        if (MaximumResolution < MinimumResolution)
        {
            yield return new ValidationResult(
                "maximumResolution must be greater than or equal to minimumResolution",
                [nameof(MaximumResolution), nameof(MinimumResolution)]);
        }

        if (Sensors is null)
        {
            yield return new ValidationResult("sensors cannot be null", [nameof(Sensors)]);
        }
        else if (Sensors.Any(sensor => sensor.Value is null))
        {
            yield return new ValidationResult("sensor value lists cannot be null", [nameof(Sensors)]);
        }

        if (TenantsInfo is null || TenantsInfo.Count == 0)
        {
            yield return new ValidationResult(
                "tenantsInfo must contain at least one tenant",
                [nameof(TenantsInfo)]);
        }
        else
        {
            foreach (var result in ValidateTenants(TenantsInfo))
            {
                yield return result;
            }
        }

        if (string.IsNullOrWhiteSpace(LocationWkt))
        {
            yield return new ValidationResult(
                "locationWkt is required",
                [nameof(LocationWkt)]);
        }
    }

    private static IEnumerable<ValidationResult> ValidateTenants(IReadOnlyList<TenantInfo> tenantsInfo)
    {
        for (var tenantIndex = 0; tenantIndex < tenantsInfo.Count; tenantIndex++)
        {
            var tenant = tenantsInfo[tenantIndex];
            if (tenant is null || string.IsNullOrWhiteSpace(tenant.TenantId))
            {
                yield return new ValidationResult(
                    $"tenantsInfo[{tenantIndex}].tenantId cannot be empty",
                    [nameof(TenantsInfo)]);
            }

            if (tenant?.TilingConfigs is null || tenant.TilingConfigs.Count == 0)
            {
                yield return new ValidationResult(
                    $"tenantsInfo[{tenantIndex}].tilingConfigs must contain at least one configuration",
                    [nameof(TenantsInfo)]);
                continue;
            }

            for (var tilingIndex = 0; tilingIndex < tenant.TilingConfigs.Count; tilingIndex++)
            {
                var tiling = tenant.TilingConfigs[tilingIndex];
                if (tiling is null || tiling.TileSizeWidth <= 0 || tiling.TileSizeHeight <= 0)
                {
                    yield return new ValidationResult(
                        $"tenantsInfo[{tenantIndex}].tilingConfigs[{tilingIndex}] tile dimensions must be greater than 0",
                        [nameof(TenantsInfo)]);
                }

                if (tiling is not null &&
                    (tiling.TileOverlapWidth < 0 || tiling.TileOverlapHeight < 0))
                {
                    yield return new ValidationResult(
                        $"tenantsInfo[{tenantIndex}].tilingConfigs[{tilingIndex}] tile overlaps cannot be negative",
                        [nameof(TenantsInfo)]);
                }
            }
        }
    }
}
