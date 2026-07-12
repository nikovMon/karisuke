using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Contracts.Messages;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class RuleMatcher
{
    public IReadOnlyList<RuleMatchResult> Match(
        GatewayInputMessage input,
        IReadOnlyList<ActiveRule> activeRules)
    {
        var matches = new List<RuleMatchResult>();

        foreach (var activeRule in activeRules)
        {
            var rule = activeRule.Rule;
            if (!rule.IsActive ||
                !MatchesSensor(input, rule) ||
                !MatchesResolution(input, rule))
            {
                continue;
            }

            if (!input.Geometry.Intersects(activeRule.Geometry))
            {
                continue;
            }

            var intersection = input.Geometry.Intersection(activeRule.Geometry);
            if (intersection.IsEmpty)
            {
                continue;
            }

            matches.Add(new RuleMatchResult(rule, intersection));
        }

        return matches;
    }

    private static bool MatchesSensor(GatewayInputMessage input, RuleDto rule)
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

        if (!string.IsNullOrWhiteSpace(input.SensorType))
        {
            return false;
        }

        return rule.Sensors.Values.Any(values => values.Contains(input.SensorName, StringComparer.Ordinal));
    }

    private static bool MatchesResolution(GatewayInputMessage input, RuleDto rule) =>
        input.Resolution >= rule.MinimumResolution && input.Resolution <= rule.MaximumResolution;
}
