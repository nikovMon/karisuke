using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RuleEngine.Rules;

public static class RuleEvaluationLogging
{
    public const string MatchedRulesField = "findair.rules.matched";
    public const string MissedRulesField = "findair.rules.missed";

    /// <summary>
    /// Logs which rules an image matched and why every other rule did not. Call it inside a scope
    /// that carries the image and pipeline IDs, so one search by image ID explains its routing.
    /// </summary>
    public static void LogRulesEvaluated(this ILogger logger, RuleEvaluation evaluation)
    {
        using (logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(MatchedRulesField, evaluation.Matches.Select(match => match.Rule.Id).ToArray()),
                   // "ruleId: reason", one entry per rule that did not match.
                   new(MissedRulesField, evaluation.Misses.Select(miss => $"{miss.RuleId}: {miss.Reason}").ToArray())
               }))
        {
            logger.RulesEvaluated();
        }
    }
}
