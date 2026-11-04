using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace ImagingPipeline.DlqRecovery.Tests;

/// <summary>Requires RabbitMQ 3.12.11 on localhost:5672 and :5673 (admin/admin); never silently skipped.</summary>
[Trait("Category", "Broker")]
public sealed class RecoveryBrokerTests
{
    [Fact]
    public async Task CrossBrokerReplayPreservesBodyAndTypedHeadersAndRemovesSourceAfterConfirmation()
    {
        await using var topology = await Topology.CreateAsync();
        var message = RecoveryPolicyTests.Delivery();
        message.Properties.Headers!["x-updated-fields"] = new object[] { System.Text.Encoding.UTF8.GetBytes("gridType") };
        await topology.SeedAsync(message);
        Assert.Equal(0, await topology.Runner().RunAsync());
        Assert.Equal(0u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.SourceQueue)).MessageCount);
        var replayed = await topology.TargetChannel.BasicGetAsync(topology.Mapping.ReplayQueue, true);
        Assert.NotNull(replayed);
        Assert.Equal(message.Body, replayed.Body.ToArray());
        Assert.Equal("original-id", replayed.BasicProperties.MessageId);
        Assert.Equal("original-correlation", replayed.BasicProperties.CorrelationId);
        Assert.Equal(1, replayed.BasicProperties.Headers!["redrive-count"]);
        Assert.Equal(0, replayed.BasicProperties.Headers["retry-count"]);
        Assert.IsType<List<object>>(replayed.BasicProperties.Headers["x-updated-fields"]);
    }

    [Fact]
    public async Task InvalidAndExhaustedOriginalsStayOnSourceBrokerInManualQueue()
    {
        await using var topology = await Topology.CreateAsync();
        var invalid = RecoveryPolicyTests.Delivery(RabbitMqClient.RabbitMqFailureKind.InvalidData);
        var exhausted = RecoveryPolicyTests.Delivery();
        exhausted.Properties.Headers!["redrive-count"] = 3;
        await topology.SeedAsync(invalid);
        await topology.SeedAsync(exhausted);
        Assert.Equal(0, await topology.Runner().RunAsync());
        Assert.Equal(0u, (await topology.TargetChannel.QueueDeclarePassiveAsync(topology.Mapping.ReplayQueue)).MessageCount);
        var one = await topology.SourceChannel.BasicGetAsync(topology.Mapping.ManualQueue, true);
        var two = await topology.SourceChannel.BasicGetAsync(topology.Mapping.ManualQueue, true);
        Assert.NotNull(one);
        Assert.NotNull(two);
        Assert.Equal(invalid.Body, one.Body.ToArray());
        Assert.Equal(exhausted.Body, two.Body.ToArray());
        Assert.Null(one.BasicProperties.Expiration);
        Assert.NotNull(two.BasicProperties.Headers!["failure-message"]);
    }

    [Fact]
    public async Task UnroutableTargetPublicationLeavesOriginalInSourceDlq()
    {
        await using var topology = await Topology.CreateAsync();
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        await using (var session = await topology.Factory.OpenAsync(topology.Mapping, CancellationToken.None))
        {
            var delivery = await session.ReceiveAsync(CancellationToken.None);
            await topology.TargetChannel.QueueDeleteAsync(topology.Mapping.ReplayQueue);
            var prepared = RecoveryPolicy.Prepare(delivery, RecoveryPolicy.Decide(delivery, topology.Options),
                topology.Mapping, "test", DateTimeOffset.UtcNow);
            await Assert.ThrowsAnyAsync<Exception>(() => session.PublishReplayAsync(prepared, CancellationToken.None));
        }
        Assert.Equal(1u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.SourceQueue)).MessageCount);
    }

    [Fact]
    public async Task CrashAfterConfirmedPublishBeforeAckLeavesBothCopiesRecoverable()
    {
        await using var topology = await Topology.CreateAsync();
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        await using (var session = await topology.Factory.OpenAsync(topology.Mapping, CancellationToken.None))
        {
            var delivery = await session.ReceiveAsync(CancellationToken.None);
            await session.PublishReplayAsync(RecoveryPolicy.Prepare(delivery, RecoveryPolicy.Decide(delivery, topology.Options),
                topology.Mapping, "test", DateTimeOffset.UtcNow), CancellationToken.None);
            // Simulate the process dying before the source ACK. Dispose closes its consumer connection.
        }
        Assert.Equal(1u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.SourceQueue)).MessageCount);
        Assert.Equal(1u, (await topology.TargetChannel.QueueDeclarePassiveAsync(topology.Mapping.ReplayQueue)).MessageCount);
    }

    [Fact]
    public async Task ConcurrentManagedRunCannotOwnSameDlq()
    {
        await using var topology = await Topology.CreateAsync();
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        await using var first = await topology.Factory.OpenAsync(topology.Mapping, CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => topology.Factory.OpenAsync(topology.Mapping, CancellationToken.None));
        var original = await first.ReceiveAsync(CancellationToken.None);
        Assert.Equal("original-id", original.Properties.MessageId);
    }

    [Fact]
    public async Task UnknownLegacyHeadIsRetainedThenTransientMessageReplays()
    {
        await using var topology = await Topology.CreateAsync(sameBroker: true);
        await topology.SeedAsync(new RecoveryDelivery(0, [1, 2, 3], new BasicProperties { MessageId = "legacy" }));
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        Assert.Equal(0, await topology.Runner().RunAsync());
        Assert.Equal(1u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.ManualQueue)).MessageCount);
        Assert.Equal(1u, (await topology.TargetChannel.QueueDeclarePassiveAsync(topology.Mapping.ReplayQueue)).MessageCount);
    }

    [Fact]
    public async Task DestinationOutageDoesNotConsumeOrLoseSourceMessage()
    {
        await using var topology = await Topology.CreateAsync();
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        topology.Options.Connections["target"].Port = 1;
        topology.Options.OperationTimeoutSeconds = 2;
        Assert.Equal(1, await topology.Runner().RunAsync());
        Assert.Equal(1u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.SourceQueue)).MessageCount);
    }

    [Fact]
    public async Task FailedReplayReturnsWithPersistentBudgetAndEventuallyMovesToManual()
    {
        await using var topology = await Topology.CreateAsync();
        await topology.SeedAsync(RecoveryPolicyTests.Delivery());
        for (var count = 1; count <= topology.Options.MaxRedrives; count++)
        {
            Assert.Equal(0, await topology.Runner().RunAsync());
            var target = await topology.TargetChannel.BasicGetAsync(topology.Mapping.ReplayQueue, false);
            Assert.NotNull(target);
            Assert.Equal(count, target.BasicProperties.Headers!["redrive-count"]);
            // Models the existing confirmed cross-cluster failure handoff after handler retries exhaust.
            // Actual production bridge/bindings must also be checked at rollout.
            var properties = new BasicProperties(target.BasicProperties);
            properties.Headers!["retry-count"] = 3;
            await topology.SourceChannel.BasicPublishAsync(string.Empty, topology.Mapping.SourceQueue, true,
                properties, target.Body);
            await topology.TargetChannel.BasicAckAsync(target.DeliveryTag, false);
        }
        Assert.Equal(0, await topology.Runner().RunAsync());
        Assert.Equal(1u, (await topology.SourceChannel.QueueDeclarePassiveAsync(topology.Mapping.ManualQueue)).MessageCount);
        Assert.Equal(0u, (await topology.TargetChannel.QueueDeclarePassiveAsync(topology.Mapping.ReplayQueue)).MessageCount);
    }

    private sealed class Topology : IAsyncDisposable
    {
        private readonly IConnection _source;
        private readonly IConnection _target;
        public IChannel SourceChannel { get; }
        public IChannel TargetChannel { get; }
        public RecoveryOptions Options { get; }
        public RecoveryQueueOptions Mapping => Options.Queues["test"];
        public RabbitMqRecoverySessionFactory Factory => new(Options);

        private Topology(IConnection source, IConnection target, IChannel sourceChannel, IChannel targetChannel, RecoveryOptions options)
        {
            _source = source;
            _target = target;
            SourceChannel = sourceChannel;
            TargetChannel = targetChannel;
            Options = options;
        }

        public static async Task<Topology> CreateAsync(bool sameBroker = false)
        {
            var options = RecoveryPolicyTests.Options();
            options.Connections["source"].Username = "admin";
            options.Connections["source"].Password = "admin";
            options.Connections["target"] = new()
            {
                Host = "localhost",
                Port = sameBroker ? 5672 : 5673,
                VirtualHost = "/",
                Username = "admin",
                Password = "admin"
            };
            var suffix = Guid.NewGuid().ToString("N");
            var mapping = options.Queues["test"];
            mapping.SourceQueue = $"karisuke.recovery.{suffix}.dlq";
            mapping.ManualQueue = $"karisuke.recovery.{suffix}.manual";
            mapping.ReplayQueue = $"karisuke.recovery.{suffix}.input";
            mapping.ReplayConnection = sameBroker ? "source" : "target";
            var source = await ConnectAsync(5672);
            var target = await ConnectAsync(sameBroker ? 5672 : 5673);
            var confirm = new CreateChannelOptions(true, true);
            var sourceChannel = await source.CreateChannelAsync(confirm);
            var targetChannel = await target.CreateChannelAsync(confirm);
            var arguments = new Dictionary<string, object?> { ["x-queue-type"] = "classic" };
            await sourceChannel.QueueDeclareAsync(mapping.SourceQueue, true, false, false, arguments);
            await sourceChannel.QueueDeclareAsync(mapping.ManualQueue, true, false, false, arguments);
            await targetChannel.QueueDeclareAsync(mapping.ReplayQueue, true, false, false, arguments);
            return new Topology(source, target, sourceChannel, targetChannel, options);
        }

        public RecoveryRunner Runner() => new(Options, Factory, NullLogger<RecoveryRunner>.Instance, TimeProvider.System);

        public async Task SeedAsync(RecoveryDelivery delivery) =>
            await SourceChannel.BasicPublishAsync(string.Empty, Mapping.SourceQueue, true, delivery.Properties, delivery.Body);

        private static Task<IConnection> ConnectAsync(int port) => new ConnectionFactory
        {
            HostName = "localhost",
            Port = port,
            UserName = "admin",
            Password = "admin",
            AutomaticRecoveryEnabled = false
        }.CreateConnectionAsync();

        public async ValueTask DisposeAsync()
        {
            // Only unique queues created by this test are removed.
            if (SourceChannel.IsOpen)
            {
                await SourceChannel.QueueDeleteAsync(Mapping.SourceQueue);
                await SourceChannel.QueueDeleteAsync(Mapping.ManualQueue);
            }
            if (TargetChannel.IsOpen) await TargetChannel.QueueDeleteAsync(Mapping.ReplayQueue);
            await SourceChannel.DisposeAsync();
            await TargetChannel.DisposeAsync();
            await _source.DisposeAsync();
            await _target.DisposeAsync();
        }
    }
}
