namespace ImagingPipeline.UnifiedGateway;

/// <summary>
/// Log events with static messages, so they group by message. Identifiers, pipeline and outcome
/// details travel as structured fields in the surrounding log scope.
/// </summary>
internal static partial class UnifiedGatewayLog
{
    [LoggerMessage(6001, LogLevel.Debug, "Pipeline dispatch delivered.")]
    public static partial void DispatchDelivered(this ILogger logger);

    [LoggerMessage(6002, LogLevel.Warning, "Pipeline dispatch failed.")]
    public static partial void DispatchFailed(this ILogger logger, Exception? exception);

    [LoggerMessage(6003, LogLevel.Warning, "Dispatch delivery listener failed; the unit remains delivered.")]
    public static partial void DeliveryListenerFailed(this ILogger logger, Exception exception);

    [LoggerMessage(6010, LogLevel.Information, "Pipeline rules loaded.")]
    public static partial void PipelineRulesLoaded(this ILogger logger);

    [LoggerMessage(6011, LogLevel.Debug, "Pipeline rules refreshed.")]
    public static partial void PipelineRulesRefreshed(this ILogger logger);

    [LoggerMessage(6012, LogLevel.Warning, "Pipeline rule rejected; it will not be matched.")]
    public static partial void PipelineRuleRejected(this ILogger logger, Exception? exception);

    [LoggerMessage(6013, LogLevel.Warning, "Pipeline rule load failed; the previous rules stay active.")]
    public static partial void PipelineRulesLoadFailed(this ILogger logger, Exception exception);
}
