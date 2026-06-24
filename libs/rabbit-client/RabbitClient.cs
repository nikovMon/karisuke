namespace ImagingPipeline.RabbitClient;

internal sealed class RabbitClient : IRabbitClient
{
    private readonly IRabbitPublisher _publisher;
    private readonly IRabbitConsumer _consumer;
    private readonly IRabbitBatchConsumer _batchConsumer;

    public RabbitClient(IRabbitPublisher publisher, IRabbitConsumer consumer, IRabbitBatchConsumer batchConsumer)
    {
        _publisher = publisher;
        _consumer = consumer;
        _batchConsumer = batchConsumer;
    }

    public Task PublishAsync(string queueName, RabbitMessageEnvelope message, CancellationToken cancellationToken = default) =>
        _publisher.PublishAsync(queueName, message, cancellationToken);

    public Task PublishToInputAsync(RabbitMessageEnvelope message, CancellationToken cancellationToken = default) =>
        _publisher.PublishToInputAsync(message, cancellationToken);

    Task IRabbitConsumer.ConsumeAsync(IRabbitMessageHandler handler, CancellationToken cancellationToken) =>
        _consumer.ConsumeAsync(handler, cancellationToken);

    Task IRabbitBatchConsumer.ConsumeAsync(IRabbitBatchMessageHandler handler, CancellationToken cancellationToken) =>
        _batchConsumer.ConsumeAsync(handler, cancellationToken);
}

