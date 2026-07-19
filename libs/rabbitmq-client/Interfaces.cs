namespace ImagingPipeline.RabbitMqClient;

public interface IRabbitMqPublisher
{
    Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default);

    Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default);
    Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default);
}

public interface IRabbitMqConsumer
{
    Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken = default);
    Task ConsumeBatchAsync(IRabbitMqBatchMessageHandler handler, int batchSize, TimeSpan maxWaitTime, CancellationToken cancellationToken = default);
}

public interface IRabbitMqClient : IRabbitMqPublisher, IRabbitMqConsumer
{
}

public interface IRabbitMqMessageHandler
{
    Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default);
}

public interface IRabbitMqBatchMessageHandler
{
    Task<IReadOnlyDictionary<string, RabbitMqMessageProcessingResult>> HandleBatchAsync(
        IReadOnlyList<RabbitMqMessageEnvelope> messages,
        CancellationToken cancellationToken = default);
}
