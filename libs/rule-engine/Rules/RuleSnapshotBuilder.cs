using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.RuleEngine.Loading;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// Turns loaded rule documents into rules ready for matching. An invalid rule is rejected on its
/// own, with a reason, and never stops the others from loading.
/// </summary>
public sealed class RuleSnapshotBuilder(TimeSpan defaultMaxPhotoAge)
{
    private const int MaxValidationErrorsPerReason = 5;
    private const int MaxReasonCharacters = 512;

    public RuleSnapshot Build(RuleLoadResult load)
    {
        var rules = new List<ActiveRule>(load.Rules.Count);
        var rejections = new List<RuleRejection>(load.RejectedSources);

        foreach (var rule in load.Rules)
        {
            if (TryBuild(rule, out var activeRule, out var rejection))
            {
                rules.Add(activeRule);
            }
            else
            {
                rejections.Add(rejection);
            }
        }

        return new RuleSnapshot(rules, rejections);
    }

    private bool TryBuild(RuleDto? rule, out ActiveRule activeRule, out RuleRejection rejection)
    {
        activeRule = null!;
        rejection = null!;
        if (rule is null)
        {
            rejection = new RuleRejection("<null>", "The rule repository returned a null rule.");
            return false;
        }

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(rule, new ValidationContext(rule), validationResults, validateAllProperties: true))
        {
            rejection = new RuleRejection(RuleLabel(rule), DescribeValidationFailure(validationResults));
            return false;
        }

        Geometry geometry;
        try
        {
            geometry = ReadGeometry(rule);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or GeometryValidationException or ParseException)
        {
            rejection = new RuleRejection(RuleLabel(rule), "Its geometry could not be read.", ex);
            return false;
        }

        activeRule = new ActiveRule(
            rule.Id,
            rule.AlgorithmNames.ToArray(),
            BuildSensors(rule.Sensors),
            CopyTenants(rule.TenantsInfo),
            rule.MinimumResolution,
            rule.MaximumResolution,
            // isPhotoOld means "skip photos older than the service default".
            rule.IsPhotoOld is true ? defaultMaxPhotoAge : null,
            geometry);
        return true;
    }

    private static Geometry ReadGeometry(RuleDto rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.LocationWkt))
        {
            return GeometryUtilities.ReadWkt(rule.LocationWkt);
        }

        if (rule.LocationGeoJson is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } geoJson)
        {
            return GeometryUtilities.ReadGeoJson(geoJson);
        }

        throw new InvalidDataException("The rule does not contain geometry.");
    }

    private static Dictionary<string, SensorMatchCriteria> BuildSensors(List<SensorConfig> sensors)
    {
        var criteria = new Dictionary<string, SensorMatchCriteria>(sensors.Count, StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
            var qualityMask = sensor.RegistrationQualities is { Count: > 0 }
                ? RegistrationQualityMask.From(sensor.RegistrationQualities)
                : 0;
            var gridTypes = sensor.GridTypes is { Count: > 0 }
                ? new HashSet<string>(sensor.GridTypes, StringComparer.Ordinal)
                : null;
            criteria[sensor.Name] = new SensorMatchCriteria(qualityMask, gridTypes);
        }

        return criteria;
    }

    // A deep copy, so a snapshot never shares mutable DTOs with the loaded documents.
    private static TenantInfo[] CopyTenants(IReadOnlyList<TenantInfo> tenants) =>
        tenants.Select(tenant => new TenantInfo
        {
            TenantId = tenant.TenantId,
            TilingConfigs = tenant.TilingConfigs
                .Select(tiling => new TilingConfig
                {
                    TileSizeWidth = tiling.TileSizeWidth,
                    TileSizeHeight = tiling.TileSizeHeight,
                    TileOverlapWidth = tiling.TileOverlapWidth,
                    TileOverlapHeight = tiling.TileOverlapHeight
                })
                .ToList()
        }).ToArray();

    private static string DescribeValidationFailure(IReadOnlyList<ValidationResult> results)
    {
        var reason = string.Join(
            " | ",
            results.Take(MaxValidationErrorsPerReason).Select(result => result.ErrorMessage ?? "Rule validation failed."));
        var omitted = results.Count - MaxValidationErrorsPerReason;
        if (omitted > 0)
        {
            reason += $" | {omitted} more validation errors";
        }

        return reason.Length <= MaxReasonCharacters ? reason : string.Concat(reason.AsSpan(0, MaxReasonCharacters - 3), "...");
    }

    private static string RuleLabel(RuleDto rule) =>
        !string.IsNullOrWhiteSpace(rule.Id) ? rule.Id
        : !string.IsNullOrWhiteSpace(rule.RuleName) ? rule.RuleName
        : "<unknown>";
}

/// <summary>
/// The result of one load: the rules ready for matching, and every rule that was left out and why.
/// </summary>
public sealed record RuleSnapshot(IReadOnlyList<ActiveRule> Rules, IReadOnlyList<RuleRejection> Rejections)
{
    /// <summary>
    /// True when the load returned rules but none were usable. Publishing such a snapshot would
    /// silently route nothing, so callers keep their previous snapshot instead.
    /// </summary>
    public bool AllRulesRejected => Rules.Count == 0 && Rejections.Count > 0;
}
