using ImagingPipeline.Gateway.Configuration;
using RabbitMQ.Client;

namespace ImagingPipeline.Gateway.Application.RabbitMq;

public static class RabbitMqTopology
{
    public static async Task DeclareAsync(
        IChannel channel,
        RabbitMqSettings settings,
        CancellationToken cancellationToken)
    {
        await channel.QueueDeclareAsync(
            settings.InputQueue,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.OutputExchange))
        {
            await channel.ExchangeDeclareAsync(
                settings.OutputExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);
        }

        await channel.QueueDeclareAsync(
            settings.DlqQueueName,
            durable: true,
            exclusive: false,
            autoDelete: false,
            arguments: null,
            cancellationToken: cancellationToken);

        if (!string.IsNullOrWhiteSpace(settings.DlqExchange))
        {
            await channel.ExchangeDeclareAsync(
                settings.DlqExchange,
                ExchangeType.Direct,
                durable: true,
                autoDelete: false,
                arguments: null,
                cancellationToken: cancellationToken);

            await channel.QueueBindAsync(
                settings.DlqQueueName,
                settings.DlqExchange,
                settings.DlqRoutingKey,
                cancellationToken: cancellationToken);
        }
    }
}
