using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbConsumer;

public sealed class Worker(
    IRabbitMqConsumer consumer,
    TbMessageHandler handler,
    ILogger<Worker> logger) : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.ConsumerStarting();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await consumer.ConsumeAsync(handler, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Connection);
                    logger.ConsumerRestartAfterFailure(ex, RestartDelay.TotalSeconds);
                    try
                    {
                        await Task.Delay(RestartDelay, stoppingToken);
                    }
                    catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            logger.ConsumerStopped();
        }
    }
}
