using System.Diagnostics;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// Runs the consumer for the registered <see cref="IRabbitMqMessageHandler"/> until the host
/// stops, restarting it with backoff whenever it stops or fails. A host that consumes one input
/// registers this instead of writing its own restart loop.
/// </summary>
public sealed class RabbitMqConsumerService(
    IRabbitMqConsumer consumer,
    IRabbitMqMessageHandler handler,
    IOptions<RabbitMqClientOptions> options,
    ILogger<RabbitMqConsumerService> logger) : BackgroundService
{
    private readonly RabbitMqConsumerRestartBackoff _backoff =
        new(TimeSpan.FromSeconds(options.Value.ReconnectDelaySeconds));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var started = Stopwatch.GetTimestamp();
            var failure = await ConsumeUntilStoppedAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            var delay = _backoff.NextDelay(Stopwatch.GetElapsedTime(started));
            MessagingTelemetry.RecordConsumerRestart(failure is null ? TelemetryErrorCategory.Unknown : TelemetryErrorCategory.Connection);
            using (logger.BeginScope(new KeyValuePair<string, object?>[] { new("RestartDelaySeconds", delay.TotalSeconds) }))
            {
                RabbitMqLog.ConsumerRestartScheduled(logger, failure);
            }

            try
            {
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    // Returns the failure that stopped the consumer, or null when it stopped without one.
    private async Task<Exception?> ConsumeUntilStoppedAsync(CancellationToken stoppingToken)
    {
        try
        {
            await consumer.ConsumeAsync(handler, stoppingToken);
            return null;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }
}
