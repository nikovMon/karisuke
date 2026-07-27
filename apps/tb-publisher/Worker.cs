using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ImagingPipeline.TbPublisher;

public sealed class Worker : BackgroundService
{
    private readonly IRabbitMqConsumer _consumer;
    private readonly IRabbitMqMessageHandler _handler;
    private readonly ILogger<Worker> _logger;
    private readonly RabbitMqConsumerRestartBackoff _restartBackoff;

    public Worker(
        IRabbitMqConsumer consumer,
        IRabbitMqMessageHandler handler,
        ILogger<Worker> logger,
        IOptions<RabbitMqClientOptions> rabbitMqOptions)
    {
        _consumer = consumer;
        _handler = handler;
        _logger = logger;
        _restartBackoff = new RabbitMqConsumerRestartBackoff(
            TimeSpan.FromSeconds(rabbitMqOptions.Value.ReconnectDelaySeconds));
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var shouldRestart = false;
            var restartDelay = TimeSpan.Zero;
            var consumerStarted = Stopwatch.GetTimestamp();

            try
            {
                await _consumer.ConsumeAsync(_handler, stoppingToken);
                shouldRestart = !stoppingToken.IsCancellationRequested;
                if (shouldRestart)
                {
                    restartDelay = _restartBackoff.NextDelay(
                        Stopwatch.GetElapsedTime(consumerStarted));
                    _logger.LogWarning(
                        "TBPublisher RabbitMQ consumer exited unexpectedly; restarting in {RestartDelay}.",
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
                _logger.LogError(
                    ex,
                    "TBPublisher RabbitMQ consumer loop failed; restarting in {RestartDelay}.",
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
