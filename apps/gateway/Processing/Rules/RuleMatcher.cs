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
            if (!MatchesSensor(input, activeRule) ||
                !MatchesResolution(input, activeRule))
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

            matches.Add(new RuleMatchResult(activeRule, intersection));
        }

        return matches;
    }

    private static bool MatchesSensor(GatewayInputMessage input, ActiveRule rule)
    {
        if (rule.Sensors.Count == 0)
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(input.SensorType) &&
            rule.Sensors.TryGetValue(input.SensorType, out var typedSensors))
        {
            return typedSensors.Contains(input.SensorName);
        }

        if (!string.IsNullOrWhiteSpace(input.SensorType))
        {
            return false;
        }

        return rule.Sensors.Values.Any(values => values.Contains(input.SensorName));
    }

    private static bool MatchesResolution(GatewayInputMessage input, ActiveRule rule) =>
        input.Resolution >= rule.MinimumResolution && input.Resolution <= rule.MaximumResolution;
}
