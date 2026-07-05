using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;

namespace ImagingPipeline.TbConsumer;

public sealed class Worker(
    IRabbitMqConsumer consumer,
    TbMessageHandler handler,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("tb-consumer starting RabbitMQ consumption");
        await consumer.ConsumeAsync(handler, stoppingToken);
    }
}
