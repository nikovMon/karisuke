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
