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

        return rule.Sensors.TryGetValue(input.SensorName, out var allowedRegistrationQualities) &&
            (allowedRegistrationQualities & RegistrationQualityMask.From(input.RegistrationQuality)) != 0;
    }

    private static bool MatchesResolution(GatewayInputMessage input, ActiveRule rule) =>
        input.Resolution >= rule.MinimumResolution && input.Resolution <= rule.MaximumResolution;
}
