using ImagingPipeline.Gateway.Configuration;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.Gateway.Application.RabbitMq;

public sealed class RabbitMqConnectionFactory : IAsyncDisposable
{
    private readonly RabbitMqSettings _settings;
    private readonly SemaphoreSlim _consumerLock = new(1, 1);
    private readonly SemaphoreSlim _publisherLock = new(1, 1);
    private IConnection? _consumerConnection;
    private IConnection? _publisherConnection;
    private bool _disposed;

    public RabbitMqConnectionFactory(IOptions<RabbitMqSettings> settings)
    {
        _settings = settings.Value;
    }

    public Task<IConnection> GetConsumerConnectionAsync(CancellationToken cancellationToken) =>
        GetConnectionAsync(isPublisher: false, cancellationToken);

    public Task<IConnection> GetPublisherConnectionAsync(CancellationToken cancellationToken) =>
        GetConnectionAsync(isPublisher: true, cancellationToken);

    private async Task<IConnection> GetConnectionAsync(bool isPublisher, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var current = isPublisher ? _publisherConnection : _consumerConnection;
        if (current is { IsOpen: true })
        {
            return current;
        }

        var connectionLock = isPublisher ? _publisherLock : _consumerLock;
        await connectionLock.WaitAsync(cancellationToken);
        try
        {
            current = isPublisher ? _publisherConnection : _consumerConnection;
            if (current is { IsOpen: true })
            {
                return current;
            }

            if (current is not null)
            {
                await DisposeConnectionAsync(current);
            }

            var connection = await CreateConnectionAsync(isPublisher, cancellationToken);
            if (isPublisher)
            {
                _publisherConnection = connection;
            }
            else
            {
                _consumerConnection = connection;
            }

            return connection;
        }
        finally
        {
            connectionLock.Release();
        }
    }

    private Task<IConnection> CreateConnectionAsync(bool isPublisher, CancellationToken cancellationToken)
    {
        var clientName = string.IsNullOrWhiteSpace(_settings.ClientProvidedName)
            ? "imaging-pipeline-gateway"
            : _settings.ClientProvidedName;

        var factory = new ConnectionFactory
        {
            HostName = _settings.Host,
            Port = _settings.Port,
            UserName = _settings.UserName,
            Password = _settings.Password,
            VirtualHost = _settings.VirtualHost,
            RequestedHeartbeat = TimeSpan.FromSeconds(_settings.RequestedHeartbeatSeconds),
            AutomaticRecoveryEnabled = true,
            TopologyRecoveryEnabled = true,
            ConsumerDispatchConcurrency = 1,
            ClientProvidedName = $"{clientName}-{(isPublisher ? "publisher" : "consumer")}"
        };

        return factory.CreateConnectionAsync(cancellationToken);
    }

    private static async ValueTask DisposeConnectionAsync(IConnection connection)
    {
        try
        {
            if (connection.IsOpen)
            {
                await connection.CloseAsync();
            }
        }
        catch
        {
        }
        finally
        {
            connection.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_consumerConnection is not null)
        {
            await DisposeConnectionAsync(_consumerConnection);
        }

        if (_publisherConnection is not null)
        {
            await DisposeConnectionAsync(_publisherConnection);
        }

        _consumerLock.Dispose();
        _publisherLock.Dispose();
    }
}
