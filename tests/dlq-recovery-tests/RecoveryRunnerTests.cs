using Microsoft.Extensions.Logging.Abstractions;

namespace ImagingPipeline.DlqRecovery.Tests;

public sealed class RecoveryRunnerTests
{
    [Fact]
    public async Task ConfirmedPublicationPrecedesAckAndOnlyInitialCohortIsRead()
    {
        var session = new FakeSession(1, RecoveryPolicyTests.Delivery(), RecoveryPolicyTests.Delivery());
        Assert.Equal(0, await Runner(session).RunAsync());
        Assert.Equal(["depth", "receive", "publish", "ack", "dispose"], session.Events);
        Assert.Equal(1, session.Remaining);
    }

    [Fact]
    public async Task FailedOrUncertainPublishNeverAcknowledgesOriginal()
    {
        var session = new FakeSession(2, RecoveryPolicyTests.Delivery()) { FailPublish = true };
        Assert.Equal(1, await Runner(session).RunAsync());
        Assert.Equal(["depth", "receive", "publish", "dispose"], session.Events);
    }

    [Fact]
    public async Task AckFailureAfterConfirmedPublishStopsTheQueueWithoutInventingAnotherCopy()
    {
        var session = new FakeSession(2, RecoveryPolicyTests.Delivery()) { FailAck = true };
        Assert.Equal(1, await Runner(session).RunAsync());
        Assert.Equal(["depth", "receive", "publish", "ack", "dispose"], session.Events);
    }

    [Fact]
    public async Task InvalidHeadMovesToManualAllowingNextTransientItemToReplay()
    {
        var session = new FakeSession(2, RecoveryPolicyTests.Delivery(RabbitMqClient.RabbitMqFailureKind.InvalidData),
            RecoveryPolicyTests.Delivery());
        Assert.Equal(0, await Runner(session).RunAsync());
        Assert.Equal(["depth", "receive", "manual", "ack", "depth", "receive", "publish", "ack", "dispose"], session.Events);
    }

    [Fact]
    public async Task FullTargetInputStopsBeforeConsumingAnySourceDelivery()
    {
        var session = new FakeSession(1, RecoveryPolicyTests.Delivery()) { InputDepth = 1000 };
        Assert.Equal(0, await Runner(session).RunAsync());
        Assert.Equal(["depth", "dispose"], session.Events);
    }

    [Fact]
    public async Task UnreadableTargetInputIsNotTreatedAsEmpty()
    {
        var session = new FakeSession(1, RecoveryPolicyTests.Delivery()) { FailDepth = true };
        Assert.Equal(1, await Runner(session).RunAsync());
        Assert.Equal(["depth", "dispose"], session.Events);
    }

    [Fact]
    public async Task SameRunReturnRemainsUnacknowledgedForALaterRun()
    {
        var session = new FakeSession(2, RecoveryPolicyTests.Delivery()) { ReturnPublishedMessage = true };
        Assert.Equal(0, await Runner(session).RunAsync());
        Assert.Equal(["depth", "receive", "publish", "ack", "depth", "receive", "dispose"], session.Events);
    }

    [Fact]
    public async Task DryRunNeverOpensAConsumerOrPublishes()
    {
        var options = RecoveryPolicyTests.Options();
        options.DryRun = true;
        var factory = new FakeFactory(new FakeSession(1, RecoveryPolicyTests.Delivery()));
        Assert.Equal(0, await new RecoveryRunner(options, factory, NullLogger<RecoveryRunner>.Instance, TimeProvider.System).RunAsync());
        Assert.True(factory.Inspected);
        Assert.False(factory.Opened);
    }

    [Fact]
    public async Task CancellationBeforeStartDoesNotOpenTheQueue()
    {
        var session = new FakeSession(1, RecoveryPolicyTests.Delivery());
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.Equal(0, await Runner(session).RunAsync(stop.Token));
        Assert.Empty(session.Events);
    }

    [Fact]
    public async Task ConfiguredRateSeparatesPublications()
    {
        var session = new FakeSession(2, RecoveryPolicyTests.Delivery(), RecoveryPolicyTests.Delivery());
        var options = RecoveryPolicyTests.Options();
        options.MessagesPerSecond = 10;
        await new RecoveryRunner(options, new FakeFactory(session), NullLogger<RecoveryRunner>.Instance, TimeProvider.System).RunAsync();
        Assert.True(session.PublishTimes[1] - session.PublishTimes[0] >= TimeSpan.FromMilliseconds(85));
    }

    private static RecoveryRunner Runner(FakeSession session) => new(RecoveryPolicyTests.Options(), new FakeFactory(session),
        NullLogger<RecoveryRunner>.Instance, TimeProvider.System);

    private sealed class FakeFactory(FakeSession session) : IRecoverySessionFactory
    {
        public bool Opened { get; private set; }
        public bool Inspected { get; private set; }
        public Task<uint> InspectAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken)
        {
            Inspected = true;
            return Task.FromResult(session.InitialCount);
        }
        public Task<IRecoverySession> OpenAsync(RecoveryQueueOptions mapping, CancellationToken cancellationToken)
        {
            Opened = true;
            return Task.FromResult<IRecoverySession>(session);
        }
    }

    private sealed class FakeSession(uint count, params RecoveryDelivery[] messages) : IRecoverySession
    {
        private readonly Queue<RecoveryDelivery> _messages = new(messages);
        public List<string> Events { get; } = [];
        public List<DateTimeOffset> PublishTimes { get; } = [];
        public uint InitialCount => count;
        public int Remaining => _messages.Count;
        public uint InputDepth { get; set; }
        public bool FailPublish { get; set; }
        public bool FailAck { get; set; }
        public bool FailDepth { get; set; }
        public bool ReturnPublishedMessage { get; set; }
        public Task<RecoveryDelivery> ReceiveAsync(CancellationToken cancellationToken)
        {
            Events.Add("receive");
            return Task.FromResult(_messages.Dequeue());
        }
        public Task<uint> ReadInputDepthAsync(CancellationToken cancellationToken)
        {
            Events.Add("depth");
            if (FailDepth) throw new IOException("read failed");
            return Task.FromResult(InputDepth);
        }
        public Task PublishReplayAsync(RecoveryDelivery delivery, CancellationToken cancellationToken)
        {
            Events.Add("publish");
            PublishTimes.Add(DateTimeOffset.UtcNow);
            if (FailPublish) throw new IOException("confirmation uncertain");
            if (ReturnPublishedMessage) _messages.Enqueue(delivery);
            return Task.CompletedTask;
        }
        public Task PublishManualAsync(RecoveryDelivery delivery, CancellationToken cancellationToken)
        {
            Events.Add("manual");
            if (FailPublish) throw new IOException("confirmation uncertain");
            return Task.CompletedTask;
        }
        public Task AckAsync(ulong deliveryTag, CancellationToken cancellationToken)
        {
            Events.Add("ack");
            if (FailAck) throw new IOException("ack failed");
            return Task.CompletedTask;
        }
        public ValueTask DisposeAsync()
        {
            Events.Add("dispose");
            return ValueTask.CompletedTask;
        }
    }
}
