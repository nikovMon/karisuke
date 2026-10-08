using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

/// <summary>
/// A v2 rule, stored in its pipeline's own index. <see cref="Match"/> decides which images
/// qualify. Each <see cref="RunParams"/> entry is one run of the pipeline, in the shape the
/// pipeline's contract defines; the gateway carries it without interpreting it.
/// </summary>
public sealed class PipelineRuleDocument : IValidatableObject
{
    public const int CurrentSchemaVersion = 2;

    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    [JsonPropertyName("ruleName")]
    public string RuleName { get; set; } = string.Empty;

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("isActive")]
    public bool IsActive { get; set; } = true;

    /// <summary>Metadata only; never matched.</summary>
    [JsonPropertyName("area")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Area { get; set; }

    /// <summary>
    /// Must be set for a rule with no conditions, so a rule that matches every image is always
    /// deliberate and never the result of clearing the last condition.
    /// </summary>
    [JsonPropertyName("matchAll")]
    public bool MatchAll { get; set; }

    [JsonPropertyName("match")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RuleMatchConditions? Match { get; set; }

    [JsonPropertyName("runParams")]
    public List<JsonElement>? RunParams { get; set; }

    [JsonPropertyName("createdBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CreatedBy { get; set; }

    [JsonPropertyName("updatedBy")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? UpdatedBy { get; set; }

    [JsonPropertyName("creationTime")]
    public DateTimeOffset CreationTime { get; set; }

    [JsonPropertyName("updateTime")]
    public DateTimeOffset UpdateTime { get; set; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (SchemaVersion != CurrentSchemaVersion)
        {
            yield return Error($"schemaVersion must be {CurrentSchemaVersion}", nameof(SchemaVersion));
        }

        if (string.IsNullOrWhiteSpace(RuleName))
        {
            yield return Error("ruleName cannot be empty", nameof(RuleName));
        }

        var hasConditions = Match?.HasConditions == true;
        if (!hasConditions && !MatchAll)
        {
            yield return Error("match must contain at least one condition; set matchAll to true to match every image", nameof(Match));
        }

        if (hasConditions && MatchAll)
        {
            yield return Error("matchAll cannot be combined with match conditions", nameof(MatchAll));
        }

        foreach (var error in Match?.Validate() ?? [])
        {
            yield return Error(error, nameof(Match));
        }

        if (RunParams is not { Count: > 0 })
        {
            yield return Error("runParams must contain at least one entry", nameof(RunParams));
        }
        else
        {
            for (var index = 0; index < RunParams.Count; index++)
            {
                if (RunParams[index].ValueKind != JsonValueKind.Object)
                {
                    yield return Error($"runParams[{index}] must be an object", nameof(RunParams));
                }
            }
        }
    }

    private static ValidationResult Error(string message, string member) => new(message, [member]);
}

/// <summary>
/// The conditions an image must meet, all of them. An absent condition means no constraint; an
/// empty collection is rejected rather than guessed at, so "any" is always expressed by omission.
/// </summary>
public sealed class RuleMatchConditions
{
    /// <summary>Each sensor with its own allowed qualities and grid types. Absent means any sensor.</summary>
    [JsonPropertyName("sensors")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<SensorConfig>? Sensors { get; set; }

    [JsonPropertyName("resolution")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ResolutionRange? Resolution { get; set; }

    /// <summary>The rule's area. The image must intersect it; the intersection becomes the ROI.</summary>
    [JsonPropertyName("locationWkt")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? LocationWkt { get; set; }

    /// <summary>GeoJSON derived from <see cref="LocationWkt"/> for Elasticsearch queries. Never matched.</summary>
    [JsonPropertyName("location")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Location { get; set; }

    [JsonPropertyName("maxPhotoAgeDays")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxPhotoAgeDays { get; set; }

    [JsonIgnore]
    public bool HasConditions =>
        Sensors is not null || Resolution is not null || LocationWkt is not null || MaxPhotoAgeDays is not null;

    internal IEnumerable<string> Validate()
    {
        if (Sensors is { Count: 0 })
        {
            yield return "match.sensors cannot be empty; omit it to accept any sensor";
        }
        else if (Sensors is not null)
        {
            foreach (var error in SensorConfig.ValidateCollection(Sensors))
            {
                yield return $"match.{error}";
            }
        }

        foreach (var error in Resolution?.Validate() ?? [])
        {
            yield return error;
        }

        if (LocationWkt is not null && string.IsNullOrWhiteSpace(LocationWkt))
        {
            yield return "match.locationWkt cannot be empty; omit it to accept any location";
        }

        if (Location is not null && LocationWkt is null)
        {
            yield return "match.location is derived from match.locationWkt and cannot be set without it";
        }

        if (MaxPhotoAgeDays is <= 0)
        {
            yield return "match.maxPhotoAgeDays must be greater than 0";
        }
    }
}

/// <summary>Allowed best-resolution range, inclusive. Either bound may be omitted, not both.</summary>
public sealed class ResolutionRange
{
    [JsonPropertyName("minimum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Minimum { get; set; }

    [JsonPropertyName("maximum")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Maximum { get; set; }

    internal IEnumerable<string> Validate()
    {
        if (Minimum is null && Maximum is null)
        {
            yield return "match.resolution must set minimum, maximum or both; omit it to accept any resolution";
        }

        if (Minimum is { } minimum && (!double.IsFinite(minimum) || minimum <= 0))
        {
            yield return "match.resolution.minimum must be a positive finite number";
        }

        if (Maximum is { } maximum && (!double.IsFinite(maximum) || maximum <= 0))
        {
            yield return "match.resolution.maximum must be a positive finite number";
        }

        if (Minimum > Maximum)
        {
            yield return "match.resolution.maximum must be greater than or equal to minimum";
        }
    }
}
