using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbConsumer;

internal static partial class TbConsumerLog
{
    [LoggerMessage(4000, LogLevel.Information, "TB Consumer RabbitMQ consumer is starting.")]
    public static partial void ConsumerStarting(this ILogger logger);

    [LoggerMessage(4001, LogLevel.Information, "TB Consumer RabbitMQ consumer has stopped.")]
    public static partial void ConsumerStopped(this ILogger logger);

    [LoggerMessage(4010, LogLevel.Warning, "TB Consumer rejected an input message because JSON deserialization failed: {ValidationError}")]
    public static partial void DeserializationRejected(this ILogger logger, string validationError);

    [LoggerMessage(4011, LogLevel.Warning, "TB Consumer rejected an input message because validation failed: {ValidationError}")]
    public static partial void MessageRejected(this ILogger logger, string validationError);

    [LoggerMessage(4012, LogLevel.Warning, "TB Consumer encountered an invalid algorithm value and RabbitMQ will retry the message: {AlgorithmValue}")]
    public static partial void InvalidAlgorithmScheduledForRetry(
        this ILogger logger,
        string algorithmValue);

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
}
