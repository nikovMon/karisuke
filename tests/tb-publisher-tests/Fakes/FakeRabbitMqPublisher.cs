using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.TbPublisher.Tests.Fakes;

public sealed class FakeRabbitMqPublisher : IRabbitMqPublisher
{
    public List<RabbitMqMessageEnvelope> PublishedToOutput { get; } = [];

    public Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default)
    {
        PublishedToOutput.Add(message);
        return Task.CompletedTask;
    }
}
