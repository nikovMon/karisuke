using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RuleEngine.Rules;

public static class RuleEvaluationLogging
{
    /// <summary>
    /// Logs which rules an image matched and why the others did not, one field per reason. Call it
    /// inside a scope that carries the image and pipeline IDs, so a search by image ID explains its
    /// routing. Geometry misses are only counted: they are usually most rules, and log lists are
    /// capped, so listing them would push out the IDs that matter.
    /// </summary>
    public static void LogRulesEvaluated(this ILogger logger, RuleEvaluation evaluation)
    {
        using (logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RulesMatchedIds, evaluation.Matches.Select(match => match.Rule.Id).ToArray()),
                   new(TelemetryAttributeNames.RulesMissedPhotoAgeIds, MissedIds(RuleMissReason.PhotoAge)),
                   new(TelemetryAttributeNames.RulesMissedPhotoAgeCount, MissedCount(RuleMissReason.PhotoAge)),
                   new(TelemetryAttributeNames.RulesMissedSensorIds, MissedIds(RuleMissReason.Sensor)),
                   new(TelemetryAttributeNames.RulesMissedSensorCount, MissedCount(RuleMissReason.Sensor)),
                   new(TelemetryAttributeNames.RulesMissedResolutionIds, MissedIds(RuleMissReason.Resolution)),
                   new(TelemetryAttributeNames.RulesMissedResolutionCount, MissedCount(RuleMissReason.Resolution)),
                   new(TelemetryAttributeNames.RulesMissedGeometryCount, MissedCount(RuleMissReason.Geometry))
               }))
        {
            logger.RulesEvaluated();
        }

        string[] MissedIds(RuleMissReason reason) =>
            evaluation.Misses.Where(miss => miss.Reason == reason).Select(miss => miss.RuleId).ToArray();

        int MissedCount(RuleMissReason reason) =>
            evaluation.Misses.Count(miss => miss.Reason == reason);
    }
}
