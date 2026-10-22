using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.RuleMigrator;

/// <summary>A rule as stored in the old Algo system's <c>algo-rules</c> index.</summary>
internal sealed class AlgoRuleDocument
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("rule_name")]
    public string? RuleName { get; set; }

    [JsonPropertyName("hebrew_rule_name")]
    public string? HebrewRuleName { get; set; }

    [JsonPropertyName("algorithm_name")]
    public string? AlgorithmName { get; set; }

    [JsonPropertyName("customer")]
    public string? Customer { get; set; }

    [JsonPropertyName("profile_name")]
    public string? ProfileName { get; set; }

    [JsonPropertyName("username")]
    public string? Username { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; set; } = true;

    [JsonPropertyName("is_photo_old")]
    public bool? IsPhotoOld { get; set; }

    [JsonPropertyName("location_wkt")]
    public string? LocationWkt { get; set; }

    [JsonPropertyName("minimum_resolution")]
    public double? MinimumResolution { get; set; }

    [JsonPropertyName("maximum_resolution")]
    public double? MaximumResolution { get; set; }

    [JsonPropertyName("sensor_types")]
    public List<string>? SensorTypes { get; set; }

    /// <summary>Each allowed sensor name with its allowed grid types; an empty list means any grid type.</summary>
    [JsonPropertyName("sensor_names")]
    public Dictionary<string, List<string>?>? SensorNames { get; set; }

    [JsonPropertyName("creation_time")]
    public DateTimeOffset CreationTime { get; set; }

    [JsonPropertyName("update_time")]
    public DateTimeOffset UpdateTime { get; set; }
}

/// <summary>
/// Converts an Algo rule to a DeepWisdom v2 rule. <c>location_geojson</c> is not copied: the
/// location is derived from the WKT once the rule passes validation. Algo rules have no
/// registration qualities, so each sensor carries only its grid types.
/// </summary>
internal static class AlgoRuleConverter
{
    public static PipelineRuleDocument Convert(AlgoRuleDocument rule, int maxPhotoAgeDays) => new()
    {
        Id = rule.Id,
        RuleName = rule.RuleName ?? string.Empty,
        IsActive = rule.IsActive,
        Match = new RuleMatchConditions
        {
            Sensors = rule.SensorNames is { Count: > 0 } sensors
                ? [.. sensors.Select(sensor => new SensorConfig { Name = sensor.Key, GridTypes = sensor.Value ?? [] })]
                : null,
            SensorTypes = rule.SensorTypes is { Count: > 0 } ? rule.SensorTypes : null,
            Resolution = Resolution(rule.MinimumResolution, rule.MaximumResolution),
            LocationWkt = string.IsNullOrWhiteSpace(rule.LocationWkt) ? null : rule.LocationWkt,
            MaxPhotoAgeDays = rule.IsPhotoOld == true ? maxPhotoAgeDays : null
        },
        RunParams = [RunParams(rule)],
        CreatedBy = RuleConverter.MigrationActor,
        UpdatedBy = RuleConverter.MigrationActor,
        CreationTime = rule.CreationTime,
        UpdateTime = rule.UpdateTime
    };

    // A minimum of 0 or less is no lower bound, since every image resolution is positive.
    private static ResolutionRange? Resolution(double? minimum, double? maximum)
    {
        var min = minimum is > 0 ? minimum : null;
        return min is null && maximum is null ? null : new ResolutionRange { Min = min, Max = maximum };
    }

    // The flags have no Algo source and start as false; set them per rule through the Rules API.
    private static JsonElement RunParams(AlgoRuleDocument rule) =>
        JsonSerializer.SerializeToElement(new
        {
            customer = rule.Customer,
            modelName = rule.ProfileName,
            hebrewRuleName = rule.HebrewRuleName,
            algorithmName = rule.AlgorithmName,
            priority = rule.Priority,
            username = rule.Username,
            runEveryOtherImage = false,
            shouldCheckInVip = false
        });
}
