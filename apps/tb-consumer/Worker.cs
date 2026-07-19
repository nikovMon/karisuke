using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;

namespace ImagingPipeline.TbConsumer;

public sealed class Worker(
    IRabbitMqConsumer consumer,
    TbMessageHandler handler) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await consumer.ConsumeAsync(handler, stoppingToken);
    }
}
