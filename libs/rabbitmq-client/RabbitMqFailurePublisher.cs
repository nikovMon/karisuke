using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient;

internal interface IRabbitMqFailurePublisher
{
    Task PublishAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken);
}

internal sealed class RabbitMqFailurePublisher(
    IRabbitMqDestinationPublisher publisher,
    IOptions<RabbitMqClientOptions> options) : IRabbitMqFailurePublisher
{
    public Task PublishAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var remote = settings.FailureDestination?.Connection;
        var input = RabbitMqConnectionSettings.Input(settings);
        var destination = new RabbitMqDestination
        {
            Connection = remote is null
                ? new("failure-input", input.Host, input.Port, input.Username, input.Password, input.VirtualHost)
                : new("failure-destination", remote.Host, remote.Port, remote.Username, remote.Password, remote.VirtualHost),
            QueueName = settings.FailureDestination?.Queue ?? settings.EffectiveDeadLetterQueue,
            DeclareTopology = false
        };
        return publisher.PublishAsync(destination, message, cancellationToken);
    }
}
