namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqClient : IRabbitMqClient
{
    private readonly IRabbitMqPublisher _publisher;
    private readonly IRabbitMqConsumer _consumer;

    public RabbitMqClient(IRabbitMqPublisher publisher, IRabbitMqConsumer consumer)
    {
        _publisher = publisher;
        _consumer = consumer;
    }

    public Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default) =>
        _publisher.PublishAsync(exchange, routingKey, message, cancellationToken);

    public Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        _publisher.PublishToInputAsync(message, cancellationToken);

    public Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        _publisher.PublishToOutputAsync(message, cancellationToken);

    Task IRabbitMqConsumer.ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken) =>
        _consumer.ConsumeAsync(handler, cancellationToken);

    Task IRabbitMqConsumer.ConsumeBatchAsync(IRabbitMqBatchMessageHandler handler, int batchSize, TimeSpan maxWaitTime, CancellationToken cancellationToken) =>
        _consumer.ConsumeBatchAsync(handler, batchSize, maxWaitTime, cancellationToken);
}

