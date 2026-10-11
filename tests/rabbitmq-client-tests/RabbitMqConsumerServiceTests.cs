using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqConsumerServiceTests
{
    [Fact]
    public async Task FailedConsumerIsRestartedAndTheRestartIsLogged()
    {
        var failure = new InvalidOperationException("channel closed");
        var consumer = new ScriptedConsumer(failure);
        var logger = new RecordingLogger();
        using var service = new RabbitMqConsumerService(
            consumer,
            new NoopHandler(),
            Options.Create(new RabbitMqClientOptions { ReconnectDelaySeconds = 1 }),
            logger);

        await service.StartAsync(CancellationToken.None);
        await consumer.SecondRun.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await service.StopAsync(CancellationToken.None);

        var restart = Assert.Single(logger.Entries);
        Assert.Equal(115, restart.EventId);
        Assert.Equal("RabbitMQ consumer stopped unexpectedly; restarting after a delay.", restart.Message);
        Assert.Same(failure, restart.Exception);
    }

    // Fails on the first run; on the second, waits until the service stops.
    private sealed class ScriptedConsumer(Exception firstRunFailure) : IRabbitMqConsumer
    {
        private int _runs;

        public TaskCompletionSource SecondRun { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _runs) == 1)
            {
                throw firstRunFailure;
            }

            SecondRun.TrySetResult();
            await Task.Delay(Timeout.Infinite, cancellationToken);
        }

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopHandler : IRabbitMqMessageHandler
    {
        public Task<RabbitMqMessageProcessingResult> HandleAsync(
            RabbitMqMessageEnvelope message,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(RabbitMqMessageProcessingResult.Success());
    }

    private sealed record LogEntry(int EventId, string Message, Exception? Exception);

    private sealed class RecordingLogger : ILogger<RabbitMqConsumerService>
    {
        private readonly List<LogEntry> _entries = [];

        public IReadOnlyList<LogEntry> Entries
        {
            get
            {
                lock (_entries)
                {
                    return _entries.ToArray();
                }
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
            {
                _entries.Add(new LogEntry(eventId.Id, formatter(state, exception), exception));
            }
        }
    }
}
