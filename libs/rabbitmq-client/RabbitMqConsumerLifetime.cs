using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqConsumerTerminatedException : Exception
{
    public RabbitMqConsumerTerminatedException(string message)
        : base(message)
    {
    }

    public RabbitMqConsumerTerminatedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class RabbitMqConsumerLifetime
{
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public void ConsumerCancelled(IReadOnlyCollection<string> consumerTags, bool connectionIsOpen)
    {
        if (!connectionIsOpen)
        {
            // An automatically recoverable connection shutdown also unregisters its consumers.
            return;
        }

        var tags = consumerTags.Count == 0 ? "<unknown>" : string.Join(", ", consumerTags);
        _completion.TrySetException(
            new RabbitMqConsumerTerminatedException(
                $"RabbitMQ cancelled consumer '{tags}'."));
    }

    public void ChannelShutdown(ShutdownEventArgs reason, bool connectionIsOpen)
    {
        if (!connectionIsOpen)
        {
            // The automatic recovery client owns transient connection outages and restores channels/consumers.
            return;
        }

        _completion.TrySetException(
            new RabbitMqConsumerTerminatedException(
                $"RabbitMQ consumer channel shut down with code {reason.ReplyCode}: {reason.ReplyText}",
                reason.Exception ?? new InvalidOperationException(reason.ReplyText)));
    }

    public void CompletionFailed(Exception exception)
    {
        _completion.TrySetException(
            new RabbitMqConsumerTerminatedException(
                "RabbitMQ message completion failed; the consumer channel must be replaced.",
                exception));
    }

    public void CallbackFailed(Exception exception)
    {
        _completion.TrySetException(
            new RabbitMqConsumerTerminatedException(
                "A RabbitMQ consumer callback failed; the consumer channel must be replaced.",
                exception));
    }
}
