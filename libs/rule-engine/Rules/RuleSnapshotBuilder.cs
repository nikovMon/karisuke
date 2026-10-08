using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Loading;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// Turns one pipeline's loaded rule documents into rules ready for matching. A rule is checked
/// against the document rules and its run parameters against the pipeline's contract, the same
/// checks the Rules API runs on write. An invalid rule is rejected on its own, with a reason, and
/// never stops the others from loading.
/// </summary>
public static class RuleSnapshotBuilder
{
    private const int MaxErrorsPerReason = 5;
    private const int MaxReasonCharacters = 512;

    public static RuleSnapshot Build(RuleLoadResult load, IPipelineContract contract)
    {
        var rules = new List<ActiveRule>(load.Rules.Count);
        var rejections = new List<RuleRejection>(load.RejectedSources);

        foreach (var rule in load.Rules)
        {
            if (TryBuild(rule, contract, out var activeRule, out var rejection))
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

    private static bool TryBuild(
        PipelineRuleDocument? rule,
        IPipelineContract contract,
        out ActiveRule activeRule,
        out RuleRejection rejection)
    {
        activeRule = null!;
        rejection = null!;
        if (rule is null)
        {
            rejection = new RuleRejection("<null>", "The rule repository returned a null rule.");
            return false;
        }

        var errors = ValidateDocument(rule).Concat(ValidateRunParams(rule, contract)).ToList();
        if (errors.Count > 0)
        {
            rejection = new RuleRejection(RuleLabel(rule), DescribeErrors(errors));
            return false;
        }

        var match = rule.Match ?? new RuleMatchConditions();
        Geometry? geometry;
        try
        {
            geometry = match.LocationWkt is null ? null : GeometryUtilities.ReadWkt(match.LocationWkt);
        }
        catch (Exception ex) when (ex is ArgumentException or GeometryValidationException or ParseException)
        {
            rejection = new RuleRejection(RuleLabel(rule), "Its geometry could not be read.", ex);
            return false;
        }

        activeRule = new ActiveRule(
            rule.Id,
            BuildSensors(match.Sensors),
            match.Resolution?.Minimum ?? 0,
            match.Resolution?.Maximum ?? double.PositiveInfinity,
            match.MaxPhotoAgeDays is { } days ? TimeSpan.FromDays(days) : null,
            geometry,
            rule.RunParams!.ToArray());
        return true;
    }

    private static IEnumerable<string> ValidateDocument(PipelineRuleDocument rule)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(rule, new ValidationContext(rule), results, validateAllProperties: true);
        return results.Select(result => result.ErrorMessage ?? "Rule validation failed.");
    }

    private static IEnumerable<string> ValidateRunParams(PipelineRuleDocument rule, IPipelineContract contract)
    {
        var runParams = rule.RunParams ?? [];
        for (var index = 0; index < runParams.Count; index++)
        {
            foreach (var error in contract.ValidateRunParams(runParams[index]))
            {
                yield return $"runParams[{index}].{error.Field}: {error.Message}";
            }
        }
    }

    private static Dictionary<string, SensorMatchCriteria> BuildSensors(List<SensorConfig>? sensors)
    {
        var criteria = new Dictionary<string, SensorMatchCriteria>(StringComparer.Ordinal);
        foreach (var sensor in sensors ?? [])
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

    private static string DescribeErrors(IReadOnlyList<string> errors)
    {
        var reason = string.Join(" | ", errors.Take(MaxErrorsPerReason));
        var omitted = errors.Count - MaxErrorsPerReason;
        if (omitted > 0)
        {
            reason += $" | {omitted} more validation errors";
        }

        return reason.Length <= MaxReasonCharacters ? reason : string.Concat(reason.AsSpan(0, MaxReasonCharacters - 3), "...");
    }

    private static string RuleLabel(PipelineRuleDocument rule) =>
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
