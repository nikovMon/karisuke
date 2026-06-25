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
    private bool _disposed;

    public RabbitMqConnectionManager(IOptions<RabbitMqClientOptions> options, ILogger<RabbitMqConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RabbitMqConnectionManager));
        if (_connection is { IsOpen: true }) return _connection;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            if (_connection is not null) await DisposeConnectionAsync(_connection);

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
                    ConsumerDispatchConcurrency = 1,
                    ClientProvidedName = $"imagingpipeline-{Environment.ProcessId}"
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
                _connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
                RabbitMqClientDiagnostics.ConnectionRecoveries.Add(1, RabbitMqClientDiagnostics.Tag("host", _options.Host));
                _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port} vhost {VirtualHost}",
                    _options.Host, _options.Port, _options.VirtualHost);
                return _connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitMqClientDiagnostics.ConnectionFailures.Add(1, RabbitMqClientDiagnostics.Tag("host", _options.Host));
                _logger.LogWarning(ex, "RabbitMQ connection failed");
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
        if (!_disposed)
        {
            _logger.LogWarning("RabbitMQ connection shut down. Initiator: {Initiator}; code: {ReplyCode}; reason: {ReplyText}",
                args.Initiator, args.ReplyCode, args.ReplyText);
        }
        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
    {
        _logger.LogError(args.Exception, "RabbitMQ connection callback failed");
        return Task.CompletedTask;
    }

    private static async Task DisposeConnectionAsync(IConnection connection)
    {
        try { await connection.CloseAsync(); }
        catch { /* The connection is already unavailable. */ }
        connection.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_connection is not null) await DisposeConnectionAsync(_connection);
        _connectionLock.Dispose();
    }
}
