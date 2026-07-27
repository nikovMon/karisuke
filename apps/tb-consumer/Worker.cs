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
                    logger.LogWarning(
                        "TBConsumer RabbitMQ consumer exited unexpectedly; restarting in {RestartDelay}.",
                        restartDelay);
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
                logger.LogError(
                    ex,
                    "TBConsumer RabbitMQ consumer loop failed; restarting in {RestartDelay}.",
                    restartDelay);
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
}
