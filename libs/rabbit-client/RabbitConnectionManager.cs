using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitClient;

internal interface IRabbitConnectionManager : IAsyncDisposable
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

internal sealed class RabbitConnectionManager : IRabbitConnectionManager
{
    private readonly RabbitClientOptions _options;
    private readonly ILogger<RabbitConnectionManager> _logger;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    public RabbitConnectionManager(IOptions<RabbitClientOptions> options, ILogger<RabbitConnectionManager> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(RabbitConnectionManager));
        if (_connection is { IsOpen: true }) return _connection;

        await _connectionLock.WaitAsync(cancellationToken);
        try
        {
            if (_connection is { IsOpen: true }) return _connection;
            if (_connection is not null) await DisposeConnectionAsync(_connection);

            Exception? lastError = null;
            for (var attempt = 1; attempt <= _options.MaxRetryCount + 1; attempt++)
            {
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
                    RabbitClientDiagnostics.ConnectionRecoveries.Add(1, RabbitClientDiagnostics.Tag("host", _options.Host));
                    _logger.LogInformation("Connected to RabbitMQ at {Host}:{Port} vhost {VirtualHost}",
                        _options.Host, _options.Port, _options.VirtualHost);
                    return _connection;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    lastError = ex;
                    RabbitClientDiagnostics.ConnectionFailures.Add(1, RabbitClientDiagnostics.Tag("host", _options.Host));
                    _logger.LogWarning(ex, "RabbitMQ connection attempt {Attempt} of {TotalAttempts} failed",
                        attempt, _options.MaxRetryCount + 1);
                    if (attempt <= _options.MaxRetryCount)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(_options.ReconnectDelaySeconds), cancellationToken);
                    }
                }
            }

            throw new InvalidOperationException("Unable to connect to RabbitMQ after the configured retries.", lastError);
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
