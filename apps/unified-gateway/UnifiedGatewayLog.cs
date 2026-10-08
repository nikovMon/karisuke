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
}
