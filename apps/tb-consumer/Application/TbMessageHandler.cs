using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.TbConsumer.Application;

public sealed class TbMessageHandler : IRabbitMqMessageHandler
{
    public Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        // TODO: Replace with actual processing logic.
        return Task.FromResult(
            RabbitMqMessageProcessingResult.Success(message.Body));
    }
}
