using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqConsumerLifetimeTests
{
    [Fact]
    public void RecoverableConnectionShutdownDoesNotTerminateTheConsumer()
    {
        var lifetime = new RabbitMqConsumerLifetime();

        lifetime.ChannelShutdown(ShutdownReason(), connectionIsOpen: false);
        lifetime.ConsumerCancelled(["consumer-1"], connectionIsOpen: false);

        Assert.False(lifetime.Completion.IsCompleted);
    }

    [Fact]
    public async Task TerminalChannelShutdownFaultsTheConsumer()
    {
        var lifetime = new RabbitMqConsumerLifetime();

        lifetime.ChannelShutdown(ShutdownReason(), connectionIsOpen: true);

        var exception = await Assert.ThrowsAsync<RabbitMqConsumerTerminatedException>(
            () => lifetime.Completion);
        Assert.Contains("406", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task BrokerCancellationFaultsTheConsumer()
    {
        var lifetime = new RabbitMqConsumerLifetime();

        lifetime.ConsumerCancelled(["consumer-1"], connectionIsOpen: true);

        var exception = await Assert.ThrowsAsync<RabbitMqConsumerTerminatedException>(
            () => lifetime.Completion);
        Assert.Contains("consumer-1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CompletionFailureFaultsTheConsumer()
    {
        var lifetime = new RabbitMqConsumerLifetime();
        var failure = new InvalidOperationException("publish failed");

        lifetime.CompletionFailed(failure);

        var exception = await Assert.ThrowsAsync<RabbitMqConsumerTerminatedException>(
            () => lifetime.Completion);
        Assert.Same(failure, exception.InnerException);
    }

    private static ShutdownEventArgs ShutdownReason() =>
        new(
            ShutdownInitiator.Peer,
            replyCode: 406,
            replyText: "PRECONDITION_FAILED",
            classId: 60,
            methodId: 20);
}
