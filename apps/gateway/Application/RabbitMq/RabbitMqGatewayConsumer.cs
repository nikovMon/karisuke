using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.Gateway.Application.RabbitMq;

public sealed class RabbitMqGatewayConsumer
{
    private readonly RabbitMqConnectionFactory _connections;
    private readonly RabbitMqSettings _rabbitMqSettings;
    private readonly GatewaySettings _gatewaySettings;
    private readonly GatewayHealthState _healthState;

    public RabbitMqGatewayConsumer(
        RabbitMqConnectionFactory connections,
        IOptions<RabbitMqSettings> rabbitMqSettings,
        IOptions<GatewaySettings> gatewaySettings,
        GatewayHealthState healthState)
    {
        _connections = connections;
        _rabbitMqSettings = rabbitMqSettings.Value;
        _gatewaySettings = gatewaySettings.Value;
        _healthState = healthState;
    }

    public async Task ConsumeAsync(
        Func<RabbitMqGatewayDelivery, CancellationToken, Task> handler,
        CancellationToken cancellationToken)
    {
        var connection = await _connections.GetConsumerConnectionAsync(cancellationToken);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: cancellationToken);

        if (_gatewaySettings.DeclareTopology)
        {
            await RabbitMqTopology.DeclareAsync(channel, _rabbitMqSettings, cancellationToken);
        }

        await channel.BasicQosAsync(0, _rabbitMqSettings.Prefetch, global: false, cancellationToken);

        var failure = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var consumer = new AsyncEventingBasicConsumer(channel);
        consumer.ReceivedAsync += async (_, args) =>
        {
            try
            {
                var delivery = new RabbitMqGatewayDelivery(
                    channel,
                    args.DeliveryTag,
                    args.Body.ToArray(),
                    args.BasicProperties.MessageId,
                    args.BasicProperties.CorrelationId,
                    args.RoutingKey);

                await handler(delivery, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure.TrySetException(ex);
                try
                {
                    if (channel.IsOpen)
                    {
                        await channel.CloseAsync();
                    }
                }
                catch
                {
                }
            }
        };

        var consumerTag = await channel.BasicConsumeAsync(
            _rabbitMqSettings.InputQueue,
            autoAck: false,
            consumerTag: string.Empty,
            noLocal: false,
            exclusive: false,
            arguments: null,
            consumer: consumer,
            cancellationToken: cancellationToken);

        _healthState.MarkConsumerStarted();

        try
        {
            var cancellation = Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            var completed = await Task.WhenAny(cancellation, failure.Task);
            if (completed == failure.Task)
            {
                await failure.Task;
            }
        }
        finally
        {
            _healthState.MarkConsumerStopped();
            if (channel.IsOpen)
            {
                await channel.BasicCancelAsync(consumerTag, noWait: false, CancellationToken.None);
            }
        }
    }
}

public sealed class RabbitMqGatewayDelivery
{
    private readonly IChannel _channel;

    internal RabbitMqGatewayDelivery(
        IChannel channel,
        ulong deliveryTag,
        byte[] body,
        string? messageId,
        string? correlationId,
        string originalRoutingKey)
    {
        _channel = channel;
        DeliveryTag = deliveryTag;
        Body = body;
        MessageId = messageId;
        CorrelationId = correlationId;
        OriginalRoutingKey = originalRoutingKey;
    }

    public ulong DeliveryTag { get; }
    public ReadOnlyMemory<byte> Body { get; }
    public string? MessageId { get; }
    public string? CorrelationId { get; }
    public string OriginalRoutingKey { get; }

    public async Task AckAsync(CancellationToken cancellationToken) =>
        await _channel.BasicAckAsync(DeliveryTag, multiple: false, cancellationToken);
}
