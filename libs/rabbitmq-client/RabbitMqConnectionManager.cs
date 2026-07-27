using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal interface IRabbitMqConnectionManager : IAsyncDisposable
{
    Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default);
}

internal interface IRabbitMqPublisherConnectionManager : IRabbitMqConnectionManager
{
}

internal interface IRabbitMqConsumerConnectionManager : IRabbitMqConnectionManager
{
}

internal sealed class RabbitMqPublisherConnectionManager : RabbitMqConnectionManager, IRabbitMqPublisherConnectionManager
{
    public RabbitMqPublisherConnectionManager(
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqConnectionManager> logger)
        : base(options, logger, "publisher")
    {
    }
}

internal sealed class RabbitMqConsumerConnectionManager : RabbitMqConnectionManager, IRabbitMqConsumerConnectionManager
{
    public RabbitMqConsumerConnectionManager(
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqConnectionManager> logger)
        : base(options, logger, "consumer")
    {
    }
}

internal class RabbitMqConnectionManager : IRabbitMqConnectionManager
{
    private readonly RabbitMqClientOptions _options;
    private readonly ILogger<RabbitMqConnectionManager> _logger;
    private readonly string _role;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private IConnection? _connection;
    private bool _disposed;

    protected RabbitMqConnectionManager(
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqConnectionManager> logger,
        string role)
    {
        _options = options.Value;
        _logger = logger;
        _role = role;
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
                    // Parallelism is provided by multiple consumer channels, not concurrent callbacks on one channel.
                    ConsumerDispatchConcurrency = 1,
                    ClientProvidedName = $"imagingpipeline-{Environment.ProcessId}-{_role}"
                };

                _connection = await factory.CreateConnectionAsync(cancellationToken);
                _connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
                _connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
                RabbitMqClientDiagnostics.ConnectionRecoveries.Add(1, RabbitMqClientDiagnostics.Tag("host", _options.Host));
                _logger.LogInformation(
                    "Connected RabbitMQ {ConnectionRole} connection to {Host}:{Port} vhost {VirtualHost}",
                    _role,
                    _options.Host,
                    _options.Port,
                    _options.VirtualHost);
                return _connection;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RabbitMqClientDiagnostics.ConnectionFailures.Add(1, RabbitMqClientDiagnostics.Tag("host", _options.Host));
                _logger.LogWarning(ex, "RabbitMQ {ConnectionRole} connection failed", _role);
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
            _logger.LogWarning(
                "RabbitMQ {ConnectionRole} connection shut down. Initiator: {Initiator}; code: {ReplyCode}; reason: {ReplyText}",
                _role,
                args.Initiator,
                args.ReplyCode,
                args.ReplyText);
        }
        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
    {
        _logger.LogError(args.Exception, "RabbitMQ {ConnectionRole} connection callback failed", _role);
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
