using ImagingPipeline.UnifiedGateway.Dispatch;
using Microsoft.Extensions.Logging.Abstractions;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class PipelineDispatcherTests
{
    [Fact]
    public async Task UnitsAreSentInParallelOverTheirTransportAndOutcomesKeepInputOrder()
    {
        var rabbit = new FakeTransport("rabbitmq") { Delay = TimeSpan.FromMilliseconds(100) };
        var http = new FakeTransport("http");
        var dispatcher = CreateDispatcher([rabbit, http]);
        var first = Unit(RabbitMqPipeline("asd"), "first");
        var second = Unit(HttpPipeline("algo"), "second");

        var outcomes = await dispatcher.DispatchAsync([first, second], CancellationToken.None);

        Assert.Equal(["first", "second"], outcomes.Select(outcome => outcome.Unit.DispatchId));
        Assert.All(outcomes, outcome => Assert.Equal(DispatchStatus.Delivered, outcome.Status));
        Assert.Equal(["first"], rabbit.Sent);
        Assert.Equal(["second"], http.Sent);
        Assert.True(http.CompletedBefore(rabbit), "The HTTP unit should not wait for the slower RabbitMQ unit.");
    }

    [Fact]
    public async Task EmptyInputProducesNoOutcomes()
    {
        var outcomes = await CreateDispatcher([]).DispatchAsync([], CancellationToken.None);

        Assert.Empty(outcomes);
    }

    [Fact]
    public async Task UnitWithoutARegisteredTransportIsRejected()
    {
        var dispatcher = CreateDispatcher([new FakeTransport("rabbitmq")]);

        var outcome = Assert.Single(await dispatcher.DispatchAsync([Unit(HttpPipeline("algo"))], CancellationToken.None));

        Assert.Equal(DispatchStatus.Rejected, outcome.Status);
        Assert.Contains("'http'", outcome.Reason);
    }

    [Fact]
    public async Task TransportExceptionBecomesRetryableWithoutLosingOtherOutcomes()
    {
        var failure = new InvalidOperationException("unexpected");
        var dispatcher = CreateDispatcher([new FakeTransport("rabbitmq") { Throw = failure }, new FakeTransport("http")]);

        var outcomes = await dispatcher.DispatchAsync(
            [Unit(RabbitMqPipeline("asd"), "first"), Unit(HttpPipeline("algo"), "second")],
            CancellationToken.None);

        Assert.Equal(DispatchStatus.Retryable, outcomes[0].Status);
        Assert.Same(failure, outcomes[0].Exception);
        Assert.Equal(DispatchStatus.Delivered, outcomes[1].Status);
    }

    [Fact]
    public async Task TransportOutcomesArePassedThrough()
    {
        var dispatcher = CreateDispatcher([
            new FakeTransport("rabbitmq") { Result = unit => DispatchOutcome.Rejected(unit, "refused") }
        ]);

        var outcome = Assert.Single(await dispatcher.DispatchAsync([Unit(RabbitMqPipeline("asd"))], CancellationToken.None));

        Assert.Equal(DispatchStatus.Rejected, outcome.Status);
        Assert.Equal("refused", outcome.Reason);
    }

    [Fact]
    public async Task ListenersAreNotifiedOnlyForDeliveredUnits()
    {
        var listener = new RecordingListener();
        var dispatcher = CreateDispatcher(
            [
                new FakeTransport("rabbitmq"),
                new FakeTransport("http") { Result = unit => DispatchOutcome.Retryable(unit, "timeout") }
            ],
            [listener]);

        await dispatcher.DispatchAsync(
            [Unit(RabbitMqPipeline("asd"), "delivered"), Unit(HttpPipeline("algo"), "failed")],
            CancellationToken.None);

        Assert.Equal(["delivered"], listener.Delivered);
    }

    [Fact]
    public async Task ListenerFailureDoesNotChangeTheDeliveredOutcomeOrSkipOtherListeners()
    {
        var second = new RecordingListener();
        var dispatcher = CreateDispatcher(
            [new FakeTransport("rabbitmq")],
            [new RecordingListener { Throw = new InvalidOperationException("cache down") }, second]);

        var outcome = Assert.Single(await dispatcher.DispatchAsync([Unit(RabbitMqPipeline("asd"))], CancellationToken.None));

        Assert.Equal(DispatchStatus.Delivered, outcome.Status);
        Assert.Single(second.Delivered);
    }

    [Fact]
    public async Task CancellationPropagates()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var dispatcher = CreateDispatcher([new FakeTransport("rabbitmq") { Delay = TimeSpan.FromSeconds(5) }]);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => dispatcher.DispatchAsync([Unit(RabbitMqPipeline("asd"))], cancellation.Token));
    }

    [Fact]
    public void DuplicateTransportKindsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(
            () => CreateDispatcher([new FakeTransport("rabbitmq"), new FakeTransport("rabbitmq")]));
    }

    private static PipelineDispatcher CreateDispatcher(
        IDispatchTransport[] transports,
        IDispatchDeliveryListener[]? listeners = null) =>
        new(transports, listeners ?? [], NullLogger<PipelineDispatcher>.Instance);

    private sealed class FakeTransport(string kind) : IDispatchTransport
    {
        private long _completedAt;

        public string Kind => kind;
        public TimeSpan Delay { get; init; }
        public Exception? Throw { get; init; }
        public Func<DispatchUnit, DispatchOutcome> Result { get; init; } = DispatchOutcome.Delivered;
        public List<string> Sent { get; } = [];

        public async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
        {
            lock (Sent)
            {
                Sent.Add(unit.DispatchId);
            }

            if (Delay > TimeSpan.Zero)
            {
                await Task.Delay(Delay, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (Throw is not null)
            {
                throw Throw;
            }

            Interlocked.Exchange(ref _completedAt, System.Diagnostics.Stopwatch.GetTimestamp());
            return Result(unit);
        }

        public bool CompletedBefore(FakeTransport other) => _completedAt < other._completedAt;
    }

    private sealed class RecordingListener : IDispatchDeliveryListener
    {
        public List<string> Delivered { get; } = [];
        public Exception? Throw { get; init; }

        public ValueTask OnDeliveredAsync(DispatchUnit unit, CancellationToken cancellationToken)
        {
            if (Throw is not null)
            {
                return ValueTask.FromException(Throw);
            }

            lock (Delivered)
            {
                Delivered.Add(unit.DispatchId);
            }

            return ValueTask.CompletedTask;
        }
    }
}
