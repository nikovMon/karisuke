using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ImagingPipeline.TbConsumer;

public sealed class Worker(
    IRabbitMqConsumer consumer,
    TbMessageHandler handler,
    ILogger<Worker> logger,
    IOptions<RabbitMqClientOptions> rabbitMqOptions) : BackgroundService
{
    private readonly RabbitMqConsumerRestartBackoff _restartBackoff =
        new(TimeSpan.FromSeconds(rabbitMqOptions.Value.ReconnectDelaySeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.ConsumerStarting();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var shouldRestart = false;
                var restartDelay = TimeSpan.Zero;
                var consumerStarted = Stopwatch.GetTimestamp();

                try
                {
                    await consumer.ConsumeAsync(handler, stoppingToken);
                    shouldRestart = !stoppingToken.IsCancellationRequested;
                    if (shouldRestart)
                    {
                        restartDelay = _restartBackoff.NextDelay(
                            Stopwatch.GetElapsedTime(consumerStarted));
                        MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Unknown);
                        logger.ConsumerRestartScheduled(restartDelay.TotalSeconds);
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    shouldRestart = !stoppingToken.IsCancellationRequested;
                    if (!shouldRestart)
                    {
                        break;
                    }

                    restartDelay = _restartBackoff.NextDelay(
                        Stopwatch.GetElapsedTime(consumerStarted));
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Connection);
                    logger.ConsumerRestartAfterFailure(ex, restartDelay.TotalSeconds);
                }

                if (shouldRestart)
                {
                    try
                    {
                        await Task.Delay(restartDelay, stoppingToken);
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
