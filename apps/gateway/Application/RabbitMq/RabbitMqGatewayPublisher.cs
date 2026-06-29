using System.Text.Json;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Domain;
using ImagingPipeline.Gateway.Dtos.Messages;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.Gateway.Application.RabbitMq;

public sealed class RabbitMqGatewayPublisher : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly RabbitMqConnectionFactory _connections;
    private readonly RabbitMqSettings _rabbitMqSettings;
    private readonly GatewaySettings _gatewaySettings;
    private readonly SemaphoreSlim _channelLock = new(1, 1);
    private IChannel? _channel;
    private bool _disposed;

    public RabbitMqGatewayPublisher(
        RabbitMqConnectionFactory connections,
        IOptions<RabbitMqSettings> rabbitMqSettings,
        IOptions<GatewaySettings> gatewaySettings)
    {
        _connections = connections;
        _rabbitMqSettings = rabbitMqSettings.Value;
        _gatewaySettings = gatewaySettings.Value;
    }

    public Task PublishOutputAsync(
        byte[] body,
        string? correlationId,
        CancellationToken cancellationToken) =>
        PublishAsync(
            _rabbitMqSettings.OutputExchange,
            _rabbitMqSettings.OutputRoutingKey,
            body,
            correlationId,
            cancellationToken);

    public Task PublishFailureAsync(
        GatewayFailureMessage failure,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        var body = JsonSerializer.SerializeToUtf8Bytes(failure, JsonOptions);
        return PublishAsync(
            _rabbitMqSettings.DlqExchange,
            _rabbitMqSettings.DlqRoutingKey,
            body,
            correlationId,
            cancellationToken);
    }

    private async Task PublishAsync(
        string exchange,
        string routingKey,
        byte[] body,
        string? correlationId,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(routingKey))
        {
            throw new InfrastructureUnavailableException("RabbitMQ routing key must not be empty.");
        }

        var channel = await GetChannelAsync(cancellationToken);
        var properties = new BasicProperties
        {
            MessageId = Guid.NewGuid().ToString("N"),
            CorrelationId = correlationId,
            ContentType = "application/json",
            Persistent = true
        };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_rabbitMqSettings.PublisherConfirmTimeoutSeconds));

        try
        {
            await channel.BasicPublishAsync(
                exchange,
                routingKey,
                mandatory: true,
                basicProperties: properties,
                body: body,
                cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            await DropChannelAsync();
            throw new InfrastructureUnavailableException("RabbitMQ publish confirmation timed out.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await DropChannelAsync();
            throw new InfrastructureUnavailableException("RabbitMQ publish failed.", ex);
        }
    }

    private async Task<IChannel> GetChannelAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_channel is { IsOpen: true })
        {
            return _channel;
        }

        await _channelLock.WaitAsync(cancellationToken);
        try
        {
            if (_channel is { IsOpen: true })
            {
                return _channel;
            }

            if (_channel is not null)
            {
                await DisposeChannelAsync(_channel);
                _channel = null;
            }

            var connection = await _connections.GetPublisherConnectionAsync(cancellationToken);
            var channelOptions = new CreateChannelOptions(
                publisherConfirmationsEnabled: true,
                publisherConfirmationTrackingEnabled: true);
            var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);

            if (_gatewaySettings.DeclareTopology)
            {
                await RabbitMqTopology.DeclareAsync(channel, _rabbitMqSettings, cancellationToken);
            }

            _channel = channel;
            return channel;
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private async Task DropChannelAsync()
    {
        await _channelLock.WaitAsync();
        try
        {
            if (_channel is not null)
            {
                await DisposeChannelAsync(_channel);
                _channel = null;
            }
        }
        finally
        {
            _channelLock.Release();
        }
    }

    private static async ValueTask DisposeChannelAsync(IChannel channel)
    {
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
        finally
        {
            channel.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_channel is not null)
        {
            await DisposeChannelAsync(_channel);
        }

        _channelLock.Dispose();
    }
}
