using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RabbitMqClient;

internal static partial class RabbitMqLog
{
    [LoggerMessage(100, LogLevel.Information,
        "RabbitMQ consumer {ConsumerIndex}/{ConsumerConcurrency} started for {Destination} with prefetch {PrefetchCount}")]
    public static partial void ConsumerStarted(
        ILogger logger,
        int consumerIndex,
        int consumerConcurrency,
        string destination,
        ushort prefetchCount);

    [LoggerMessage(101, LogLevel.Information,
        "RabbitMQ batch consumer started for {Destination} with batch size {BatchSize} and prefetch {PrefetchCount}")]
    public static partial void BatchConsumerStarted(
        ILogger logger,
        string destination,
        int batchSize,
        ushort prefetchCount);

    [LoggerMessage(102, LogLevel.Information, "RabbitMQ consumer stopping for {Destination}")]
    public static partial void ConsumerStopping(ILogger logger, string destination);

    [LoggerMessage(103, LogLevel.Error, "RabbitMQ handler failed for message {MessageId}")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, string messageId);

    [LoggerMessage(104, LogLevel.Error, "RabbitMQ batch handler failed for {MessageCount} messages")]
    public static partial void BatchHandlerFailed(ILogger logger, Exception exception, int messageCount);

    [LoggerMessage(105, LogLevel.Warning,
        "RabbitMQ message {MessageId} scheduled for retry {RetryAttempt}/{MaxRetryAttempts} through {RetryExchange}")]
    public static partial void RetryScheduled(
        ILogger logger,
        string messageId,
        int retryAttempt,
        int maxRetryAttempts,
        string retryExchange);

    [LoggerMessage(106, LogLevel.Warning,
        "RabbitMQ message {MessageId} was rejected for dead-letter routing to {DeadLetterQueue}; reason: {Reason}")]
    public static partial void DeadLettered(
        ILogger logger,
        string messageId,
        string deadLetterQueue,
        string reason);

    [LoggerMessage(107, LogLevel.Error,
        "RabbitMQ completion routing failed for message {MessageId}; the delivery will be requeued")]
    public static partial void CompletionFailed(ILogger logger, Exception exception, string messageId);

    [LoggerMessage(108, LogLevel.Information,
        "Connected to RabbitMQ at {Host}:{Port} vhost {VirtualHost}")]
    public static partial void ConnectionEstablished(ILogger logger, string host, int port, string virtualHost);

    [LoggerMessage(109, LogLevel.Warning, "RabbitMQ connection attempt failed")]
    public static partial void ConnectionFailed(ILogger logger, Exception exception);

    [LoggerMessage(110, LogLevel.Warning,
        "RabbitMQ connection shut down; initiator {Initiator}, code {ReplyCode}, reason {ReplyText}")]
    public static partial void ConnectionShutdown(
        ILogger logger,
        object initiator,
        ushort replyCode,
        string replyText);

    [LoggerMessage(111, LogLevel.Error, "RabbitMQ connection callback failed")]
    public static partial void CallbackFailed(ILogger logger, Exception exception);

    [LoggerMessage(112, LogLevel.Information, "RabbitMQ connection recovery succeeded")]
    public static partial void RecoverySucceeded(ILogger logger);

    [LoggerMessage(113, LogLevel.Warning, "RabbitMQ connection recovery failed")]
    public static partial void RecoveryFailed(ILogger logger, Exception exception);
}
