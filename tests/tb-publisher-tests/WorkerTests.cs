using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class WorkerTests
{
    [Fact]
    public async Task ExecuteAsync_ConsumerReturnsUnexpectedly_RecordsAndLogsRestart()
    {
        var restartMeasurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = CreateRestartListener(restartMeasurements);
        var logger = new SignalingLogger<Worker>(eventId: 3003);
        var worker = new Worker(
            new ReturningConsumer(),
            new NoOpHandler(),
            logger,
            Options.Create(new RabbitMqClientOptions()));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await logger.EventRecorded.WaitAsync(TimeSpan.FromSeconds(1));

            var tags = Assert.Single(restartMeasurements);
            Assert.Contains(
                tags,
                tag => tag.Key == "error.type"
                       && string.Equals(tag.Value as string, "unknown", StringComparison.Ordinal));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static MeterListener CreateRestartListener(
        ConcurrentQueue<KeyValuePair<string, object?>[]> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == TelemetryMetricNames.RabbitMqConsumerRestarts)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            if (measurement > 0)
            {
                measurements.Enqueue(tags.ToArray());
            }
        });
        listener.Start();
        return listener;
    }

    private sealed class ReturningConsumer : IRabbitMqConsumer
    {
        public Task ConsumeAsync(
            IRabbitMqMessageHandler handler,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoOpHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RabbitMqMessageProcessingResult.Success());
    }

    private sealed class SignalingLogger<T>(int eventId) : ILogger<T>
    {
        private readonly TaskCompletionSource _eventRecorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task EventRecorded => _eventRecorded.Task;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId actualEventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (actualEventId.Id == eventId)
            {
                _eventRecorded.TrySetResult();
            }
        }
    }
}
