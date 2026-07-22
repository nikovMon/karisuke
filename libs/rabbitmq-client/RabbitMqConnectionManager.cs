using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal interface IRabbitMqConnectionManager : IAsyncDisposable
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

internal sealed class RabbitMqConnectionManager : IRabbitMqConnectionManager
{
    private readonly RabbitMqClientOptions _options;
    private readonly ILogger<RabbitMqConnectionManager> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private int _connectionCounted;
    private bool _disposed;

    public RabbitMqConnectionManager(IOptions<RabbitMqClientOptions> options, ILogger<RabbitMqConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_connection is { IsOpen: true })
        {
            return _connection;
        }

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_connection is { IsOpen: true })
            {
                return _connection;
            }

            if (_connection is not null)
            {
                await DisposeConnectionAsync(_connection);
                _connection = null;
            }

            try
            {
                var factory = new ConnectionFactory
                {
                    HostName = _options.Host,
                    Port = _options.Port,
                    UserName = _options.Username,
                    Password = _options.Password,
                    VirtualHost = _options.VirtualHost,
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    NetworkRecoveryInterval = TimeSpan.FromSeconds(_options.ReconnectDelaySeconds),
                    // Parallelism is provided by multiple consumer channels, not concurrent callbacks on one channel.
                    ConsumerDispatchConcurrency = 1,
                    ClientProvidedName =
                        $"imagingpipeline-{Environment.MachineName}-{Environment.ProcessId}"
                };

                var connection = await factory.CreateConnectionAsync(cancellationToken);
                connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
                connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
                connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
                connection.ConnectionRecoveryErrorAsync += OnConnectionRecoveryErrorAsync;
                _connection = connection;
                MarkConnectionOpen();
                MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.ConnectSuccess);
                RabbitMqLog.ConnectionEstablished(_logger, _options.Host, _options.Port, _options.VirtualHost);
                return connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MessagingTelemetry.RecordConnectionEvent(
                    RabbitMqConnectionEvent.ConnectFailure,
                    TelemetryErrorCategory.Connection);
                RabbitMqLog.ConnectionFailed(_logger, ex);
                throw;
            }
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        MarkConnectionClosed();
        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.Shutdown);
        if (!_disposed)
        {
            RabbitMqLog.ConnectionShutdown(_logger, args.Initiator, args.ReplyCode, args.ReplyText);
        }

        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
    {
        RabbitMqLog.CallbackFailed(_logger, args.Exception);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        MarkConnectionOpen();
        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.RecoverySuccess);
        RabbitMqLog.RecoverySucceeded(_logger);
        return Task.CompletedTask;
    }

    private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
    {
        MarkConnectionClosed();
        MessagingTelemetry.RecordConnectionEvent(
            RabbitMqConnectionEvent.RecoveryFailure,
            TelemetryErrorCategory.Connection);
        RabbitMqLog.RecoveryFailed(_logger, args.Exception);
        return Task.CompletedTask;
    }

    private void MarkConnectionOpen()
    {
        if (Interlocked.Exchange(ref _connectionCounted, 1) == 0)
        {
            MessagingTelemetry.AddConnection(1);
        }
    }

    private void MarkConnectionClosed()
    {
        if (Interlocked.Exchange(ref _connectionCounted, 0) == 1)
        {
            MessagingTelemetry.AddConnection(-1);
        }
    }

    private async Task DisposeConnectionAsync(IConnection connection)
    {
        try
        {
            await connection.CloseAsync();
        }
        catch
        {
            // The connection is already unavailable.
        }
        finally
        {
            MarkConnectionClosed();
            connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
            connection.CallbackExceptionAsync -= OnCallbackExceptionAsync;
            connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
            connection.ConnectionRecoveryErrorAsync -= OnConnectionRecoveryErrorAsync;
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
        await _connectionLock.WaitAsync();
        try
        {
            if (_connection is not null)
            {
                await DisposeConnectionAsync(_connection);
                _connection = null;
            }
        }
        finally
        {
            _connectionLock.Release();
            _connectionLock.Dispose();
        }
    }
}
