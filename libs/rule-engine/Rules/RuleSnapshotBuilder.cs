using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Loading;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// Turns one pipeline's loaded rule documents into rules ready for matching. Each rule is checked
/// with <see cref="PipelineRuleValidator"/>; an invalid rule is rejected on its own, with a reason,
/// and never stops the others from loading.
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

        var validation = PipelineRuleValidator.Validate(rule, contract);
        if (!validation.IsValid)
        {
            rejection = new RuleRejection(RuleLabel(rule), DescribeErrors(validation.Errors));
            return false;
        }

        var match = rule.Match ?? new RuleMatchConditions();
        activeRule = new ActiveRule(
            rule.Id,
            BuildSensors(match.Sensors),
            match.Resolution?.Min ?? 0,
            match.Resolution?.Max ?? double.PositiveInfinity,
            match.MaxPhotoAgeDays is { } days ? TimeSpan.FromDays(days) : null,
            validation.Geometry,
            rule.RunParams!.ToArray());
        return true;
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
