using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbPublisher;

public sealed class Worker : BackgroundService
{
    private static readonly TimeSpan RestartDelay = TimeSpan.FromSeconds(5);

    private readonly IRabbitMqConsumer _consumer;
    private readonly IRabbitMqMessageHandler _handler;
    private readonly ILogger<Worker> _logger;

    public Worker(IRabbitMqConsumer consumer, IRabbitMqMessageHandler handler, ILogger<Worker> logger)
    {
        _consumer = consumer;
        _handler = handler;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.ConsumerStarting();

        while (!stoppingToken.IsCancellationRequested)
        {
            var shouldRestart = false;
            try
            {
                await _consumer.ConsumeAsync(_handler, stoppingToken);
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Unknown);
                    _logger.ConsumerRestartScheduled(RestartDelay.TotalSeconds);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    MessagingTelemetry.RecordConsumerRestart(TelemetryErrorCategory.Connection);
                    _logger.ConsumerRestartAfterFailure(ex, RestartDelay.TotalSeconds);
                }
            }

            if (shouldRestart)
            {
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

        _logger.ConsumerStopped();
    }
}
