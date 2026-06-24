namespace ImagingPipeline.RabbitClient;

public interface IRabbitPublisher
{
    Task PublishAsync(string queueName, RabbitMessageEnvelope message, CancellationToken cancellationToken = default);
    Task PublishToInputAsync(RabbitMessageEnvelope message, CancellationToken cancellationToken = default);
}

public interface IRabbitConsumer
{
    Task ConsumeAsync(IRabbitMessageHandler handler, CancellationToken cancellationToken = default);
}

public interface IRabbitBatchConsumer
{
    Task ConsumeAsync(IRabbitBatchMessageHandler handler, CancellationToken cancellationToken = default);
}

public interface IRabbitClient : IRabbitPublisher, IRabbitConsumer, IRabbitBatchConsumer
{
}

public interface IRabbitMessageHandler
{
    Task<RabbitMessageProcessingResult> HandleAsync(
        RabbitMessageEnvelope message,
        CancellationToken cancellationToken = default);
}

public interface IRabbitBatchMessageHandler
{
    Task<IReadOnlyList<RabbitMessageProcessingResult>> HandleBatchAsync(
        IReadOnlyList<RabbitMessageEnvelope> messages,
        CancellationToken cancellationToken = default);
}
