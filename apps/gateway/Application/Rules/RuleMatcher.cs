using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Application.Messages;

namespace ImagingPipeline.Gateway.Application.Rules;

public sealed class RuleMatcher
{
    private readonly GeometryExtractor _geometryExtractor;

    public RuleMatcher(GeometryExtractor geometryExtractor)
    {
        _geometryExtractor = geometryExtractor;
    }

    public IReadOnlyList<RuleMatchResult> Match(
        ValidatedInputMessage input,
        IReadOnlyList<RuleConfigDto> activeRules)
    {
        var matches = new List<RuleMatchResult>();

        foreach (var rule in activeRules)
        {
            if (!rule.IsActive ||
                !MatchesSensor(input, rule) ||
                !MatchesResolution(input, rule) ||
                !MatchesLookback(input, rule))
            {
                continue;
            }

            var ruleGeometry = _geometryExtractor.ReadRuleGeometry(rule);
            if (!input.Geometry.Intersects(ruleGeometry))
            {
                continue;
            }

            var intersection = input.Geometry.Intersection(ruleGeometry);
            if (intersection.IsEmpty)
            {
                continue;
            }

            matches.Add(new RuleMatchResult(rule, intersection));
        }

        return matches;
    }

    private static bool MatchesSensor(ValidatedInputMessage input, RuleConfigDto rule)
    {
        if (rule.Sensors is null || rule.Sensors.Count == 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(input.SensorType) &&
            rule.Sensors.TryGetValue(input.SensorType, out var typedSensors))
        {
            return typedSensors.Contains(input.SensorName, StringComparer.Ordinal);
        }

        return rule.Sensors.Values.Any(values => values.Contains(input.SensorName, StringComparer.Ordinal));
    }

    private static bool MatchesResolution(ValidatedInputMessage input, RuleConfigDto rule) =>
        input.Resolution >= rule.MinResolution && input.Resolution <= rule.MaxResolution;

    private static bool MatchesLookback(ValidatedInputMessage input, RuleConfigDto rule)
    {
        if (!rule.MaxLookBackDay.HasValue)
        {
            return true;
        }

        if (!input.AcquisitionTime.HasValue)
        {
            return false;
        }

        var earliest = DateTimeOffset.UtcNow.AddDays(-rule.MaxLookBackDay.Value);
        return input.AcquisitionTime.Value.ToUniversalTime() >= earliest;
    }
}
