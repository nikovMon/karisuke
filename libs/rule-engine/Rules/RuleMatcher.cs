using ImagingPipeline.RuleEngine.Input;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// Finds the rules an image satisfies. All conditions must hold. Cheap checks run first and the
/// geometry intersection last, because it is the only expensive one. For every rule that does not
/// match, the first failed condition is reported, so callers can log why.
/// </summary>
public sealed class RuleMatcher(TimeProvider timeProvider)
{
    public RuleEvaluation Match(GatewayInputMessage input, IReadOnlyList<ActiveRule> rules)
    {
        var registrationQualityMask = RegistrationQualityMask.From(input.RegistrationQuality);
        var photoAge = timeProvider.GetUtcNow() - input.PhotoTime;
        var matches = new List<RuleMatchResult>();
        var misses = new List<RuleMiss>();

        foreach (var rule in rules)
        {
            if (!MatchesPhotoAge(photoAge, rule))
            {
                misses.Add(new RuleMiss(rule.Id, RuleMissReason.PhotoAge));
            }
            else if (!MatchesSensor(input.SensorName, registrationQualityMask, input.GridType, rule))
            {
                misses.Add(new RuleMiss(rule.Id, RuleMissReason.Sensor));
            }
            else if (!MatchesResolution(input.BestResolution, rule))
            {
                misses.Add(new RuleMiss(rule.Id, RuleMissReason.Resolution));
            }
            else if (Intersect(input.Geometry, rule.Geometry) is { } intersection)
            {
                matches.Add(new RuleMatchResult(rule, intersection));
            }
            else
            {
                misses.Add(new RuleMiss(rule.Id, RuleMissReason.Geometry));
            }
        }

        return new RuleEvaluation(matches, misses);
    }

    private static bool MatchesPhotoAge(TimeSpan photoAge, ActiveRule rule) =>
        rule.MaxPhotoAge is not { } maxPhotoAge || photoAge <= maxPhotoAge;

    // No sensors means any sensor matches.
    private static bool MatchesSensor(string sensorName, int registrationQualityMask, string gridType, ActiveRule rule)
    {
        if (rule.Sensors.Count == 0)
        {
            return true;
        }

        if (!rule.Sensors.TryGetValue(sensorName, out var criteria))
        {
            return false;
        }

        var qualityAllowed = criteria.RegistrationQualityMask == 0 ||
            (criteria.RegistrationQualityMask & registrationQualityMask) != 0;
        var gridTypeAllowed = criteria.AllowedGridTypes is not { Count: > 0 } ||
            criteria.AllowedGridTypes.Contains(gridType);
        return qualityAllowed && gridTypeAllowed;
    }

    private static bool MatchesResolution(double bestResolution, ActiveRule rule) =>
        bestResolution >= rule.MinimumResolution && bestResolution <= rule.MaximumResolution;

    // A rule without a location covers the whole image. Intersects is a cheap test; the
    // intersection itself is computed only when it can be non-empty.
    private static Geometry? Intersect(Geometry image, Geometry? rule)
    {
        if (rule is null)
        {
            return image;
        }

        if (!image.Intersects(rule))
        {
            return null;
        }

        var intersection = image.Intersection(rule);
        return intersection.IsEmpty ? null : intersection;
    }
}
