using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbConsumer;

public sealed class Worker(
    IRabbitMqConsumer consumer,
    TbMessageHandler handler,
    ILogger<Worker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.ConsumerStarting();
        try
        {
            await consumer.ConsumeAsync(handler, stoppingToken);
        }
        finally
        {
            logger.ConsumerStopped();
        }
    }
}
