using System.Diagnostics;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class RuleMatcher
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maxPhotoAge;
    private readonly int _maxPhotoAgeDays;
    private readonly ILogger<RuleMatcher> _logger;

    public RuleMatcher(
        IOptions<GatewaySettings> settings,
        TimeProvider timeProvider,
        ILogger<RuleMatcher> logger)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        _timeProvider = timeProvider;
        _maxPhotoAgeDays = settings.Value.MaxPhotoAgeDays;
        _maxPhotoAge = TimeSpan.FromDays(_maxPhotoAgeDays);
        _logger = logger;
    }

    public IReadOnlyList<RuleMatchResult> Match(
        GatewayInputMessage input,
        IReadOnlyList<ActiveRule> activeRules)
    {
        var matches = new List<RuleMatchResult>();
        var registrationQualityMask = RegistrationQualityMask.From(input.RegistrationQuality);
        var photoAge = _timeProvider.GetUtcNow() - input.PhotoTime;
        var exceedsMaximumPhotoAge = photoAge > _maxPhotoAge;
        var photoAgeFilteredRuleCount = 0;

        foreach (var activeRule in activeRules)
        {
            if (exceedsMaximumPhotoAge && activeRule.IsPhotoOld)
            {
                photoAgeFilteredRuleCount++;
                continue;
            }

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

        if (photoAgeFilteredRuleCount > 0)
        {
            var imageAgeDays = photoAge.TotalDays;
            GatewayTelemetry.RecordPhotoAgeFilteredRules(photoAgeFilteredRuleCount);
            if (Activity.Current?.IsAllDataRequested == true)
            {
                Activity.Current.SetTag(
                    "imaging_pipeline.gateway.rules.filtered_photo_age",
                    photoAgeFilteredRuleCount);
                Activity.Current.SetTag(
                    "imaging_pipeline.gateway.photo.age_days",
                    imageAgeDays);
                Activity.Current.SetTag(
                    "imaging_pipeline.gateway.photo.max_age_days",
                    _maxPhotoAgeDays);
            }

            _logger.OldPhotoRulesFiltered(
                photoAgeFilteredRuleCount,
                Math.Round(imageAgeDays, 3),
                _maxPhotoAgeDays,
                input.PhotoTime);
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
