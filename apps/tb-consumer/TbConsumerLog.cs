using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbConsumer;

internal static partial class TbConsumerLog
{
    [LoggerMessage(4000, LogLevel.Information, "TB Consumer RabbitMQ consumer is starting.")]
    public static partial void ConsumerStarting(this ILogger logger);

    [LoggerMessage(4001, LogLevel.Information, "TB Consumer RabbitMQ consumer has stopped.")]
    public static partial void ConsumerStopped(this ILogger logger);

    [LoggerMessage(4002, LogLevel.Warning, "TB Consumer RabbitMQ consumer failed; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartAfterFailure(
        this ILogger logger,
        Exception exception,
        double restartDelaySeconds);

    [LoggerMessage(4003, LogLevel.Warning, "TB Consumer RabbitMQ consumer exited unexpectedly; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartScheduled(
        this ILogger logger,
        double restartDelaySeconds);

    [LoggerMessage(4010, LogLevel.Warning, "TB Consumer rejected an input message because JSON deserialization failed: {ValidationError}")]
    public static partial void DeserializationRejected(this ILogger logger, string validationError);

    [LoggerMessage(4011, LogLevel.Warning, "TB Consumer rejected an input message because validation failed: {ValidationError}")]
    public static partial void MessageRejected(this ILogger logger, string validationError);

    [LoggerMessage(4013, LogLevel.Warning, "TB Consumer Projection Mapper processing failed for a batch of {TileCount} tiles; RabbitMQ will retry the message.")]
    public static partial void ProjectionScheduledForRetry(
        this ILogger logger,
        int tileCount);

    [LoggerMessage(4014, LogLevel.Warning, "TB Consumer output publication failed after {PublishedCount} of {TotalCount} tiles; successfully published outputs may be duplicated when RabbitMQ retries the input.")]
    public static partial void OutputPublishScheduledForRetry(
        this ILogger logger,
        int publishedCount,
        int totalCount);

    [LoggerMessage(4015, LogLevel.Debug, "TB Consumer processed {TileCount} tiles, received {MappedCoordinateCount} mapped coordinate pairs, and published {OutputCount} Embedder inputs.")]
    public static partial void MessageProcessed(
        this ILogger logger,
        int tileCount,
        int mappedCoordinateCount,
        int outputCount);

    [LoggerMessage(4016, LogLevel.Warning, "TB Consumer Projection Mapper returned {ActualResultCount} results for {ExpectedResultCount} tiles; RabbitMQ will retry the message.")]
    public static partial void ProjectionResultCountMismatchScheduledForRetry(
        this ILogger logger,
        int expectedResultCount,
        int actualResultCount);
}
