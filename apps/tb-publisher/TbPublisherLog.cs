using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbPublisher;

internal static partial class TbPublisherLog
{
    [LoggerMessage(3000, LogLevel.Information, "TB Publisher RabbitMQ consumer is starting.")]
    public static partial void ConsumerStarting(this ILogger logger);

    [LoggerMessage(3001, LogLevel.Information, "TB Publisher RabbitMQ consumer has stopped.")]
    public static partial void ConsumerStopped(this ILogger logger);

    [LoggerMessage(3002, LogLevel.Warning, "TB Publisher RabbitMQ consumer failed; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartAfterFailure(
        this ILogger logger,
        Exception exception,
        double restartDelaySeconds);

    [LoggerMessage(3003, LogLevel.Warning, "TB Publisher RabbitMQ consumer exited unexpectedly; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartScheduled(
        this ILogger logger,
        double restartDelaySeconds);

    [LoggerMessage(3010, LogLevel.Warning, "TB Publisher rejected an input message because validation failed: {ValidationErrors}")]
    public static partial void MessageRejected(this ILogger logger, string validationErrors);

    [LoggerMessage(3011, LogLevel.Warning, "TB Publisher rejected an input message because its ROI geometry is invalid.")]
    public static partial void InvalidRoiGeometry(this ILogger logger, Exception exception);

    [LoggerMessage(3012, LogLevel.Warning, "TB Publisher could not map the input geometry through Projection Mapper; RabbitMQ will retry the message.")]
    public static partial void ProjectionFailed(this ILogger logger, Exception exception);

    [LoggerMessage(3013, LogLevel.Warning, "TB Publisher rejected the Projection Mapper response because its pixel geometry is invalid.")]
    public static partial void InvalidProjectedGeometry(this ILogger logger, Exception exception);

    [LoggerMessage(3014, LogLevel.Warning, "TB Publisher output publication failed after {PublishedCount} of {TotalCount} messages; successfully published outputs may be duplicated when RabbitMQ retries the input.")]
    public static partial void OutputPublishScheduledForRetry(
        this ILogger logger,
        int publishedCount,
        int totalCount);

    [LoggerMessage(3015, LogLevel.Information, "TB Publisher processed a task with {GroundPointCount} ground points and {TilingConfigCount} tiling configurations, publishing {OutputCount} Tile Builder requests. RequestIds: {RequestIds}")]
    public static partial void MessageProcessed(
        this ILogger logger,
        int groundPointCount,
        int tilingConfigCount,
        int outputCount,
        string requestIds);
}
