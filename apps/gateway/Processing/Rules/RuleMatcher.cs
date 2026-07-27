using ImagingPipeline.Gateway.Contracts.Messages;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class RuleMatcher
{
    public IReadOnlyList<RuleMatchResult> Match(
        GatewayInputMessage input,
        IReadOnlyList<ActiveRule> activeRules)
    {
        var matches = new List<RuleMatchResult>();
        var registrationQualityMask = RegistrationQualityMask.From(input.RegistrationQuality);

        foreach (var activeRule in activeRules)
        {
            if (!MatchesSensor(input.SensorName, registrationQualityMask, activeRule) ||
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

    private static bool MatchesSensor(
        string sensorName,
        int registrationQualityMask,
        ActiveRule rule)
    {
        if (rule.Sensors.Count == 0)
        {
            return true;
        }

        return rule.Sensors.TryGetValue(sensorName, out var allowedRegistrationQualities) &&
            (allowedRegistrationQualities & registrationQualityMask) != 0;
    }

    private static bool MatchesResolution(GatewayInputMessage input, ActiveRule rule) =>
        input.BestResolution >= rule.MinimumResolution && input.BestResolution <= rule.MaximumResolution;
}
