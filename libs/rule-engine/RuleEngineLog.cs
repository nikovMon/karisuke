using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RuleEngine;

/// <summary>
/// Log events with static messages, so they group by message. Identifiers such as the pipeline
/// travel as structured fields in the caller's log scope.
/// </summary>
internal static partial class RuleEngineLog
{
    [LoggerMessage(7001, LogLevel.Warning, "Failed to close the Elasticsearch point in time; it expires on its own.")]
    public static partial void PointInTimeCloseFailed(this ILogger logger, Exception exception);

    [LoggerMessage(7002, LogLevel.Information, "Image evaluated against pipeline rules.")]
    public static partial void RulesEvaluated(this ILogger logger);
}
