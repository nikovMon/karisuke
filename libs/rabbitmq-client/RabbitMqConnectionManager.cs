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
    private static readonly TimeSpan DefaultDisposalTimeout = TimeSpan.FromSeconds(5);

    private readonly RabbitMqClientOptions _options;
    private readonly ILogger<RabbitMqConnectionManager> _logger;
    private readonly Func<CancellationToken, Task<IConnection>> _connectionFactory;
    private readonly TimeSpan _disposalTimeout;
    private readonly SemaphoreSlim _connectionLock = new(1, 1);
    private readonly CancellationTokenSource _disposalCancellation = new();
    private readonly object _disposalSync = new();
    private readonly object _connectionStateSync = new();
    private IConnection? _connection;
    private Task? _disposalTask;
    private int _connectionCounted;
    private int _disposed;

    public RabbitMqConnectionManager(IOptions<RabbitMqClientOptions> options, ILogger<RabbitMqConnectionManager> logger)
        : this(options, logger, connectionFactory: null, DefaultDisposalTimeout)
    {
    }

    internal RabbitMqConnectionManager(
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqConnectionManager> logger,
        Func<CancellationToken, Task<IConnection>>? connectionFactory,
        TimeSpan disposalTimeout)
    {
        _options = options.Value;
        _logger = logger;
        _connectionFactory = connectionFactory ?? CreateConnectionAsync;
        _disposalTimeout = disposalTimeout > TimeSpan.Zero
            ? disposalTimeout
            : throw new ArgumentOutOfRangeException(nameof(disposalTimeout));
    }

    public async Task<IConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var currentConnection = Volatile.Read(ref _connection);
        if (currentConnection is { IsOpen: true })
        {
            return currentConnection;
        }

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposalCancellation.Token);
        try
        {
            await _connectionLock.WaitAsync(connectionCancellation.Token);
        }
        catch (OperationCanceledException) when (
            _disposalCancellation.IsCancellationRequested &&
            !cancellationToken.IsCancellationRequested)
        {
            throw new ObjectDisposedException(nameof(RabbitMqConnectionManager));
        }

        try
        {
            ThrowIfDisposed();
            currentConnection = Volatile.Read(ref _connection);
            if (currentConnection is { IsOpen: true })
            {
                return currentConnection;
            }

            var staleConnection = TakeCurrentConnection();
            if (staleConnection is not null)
            {
                await DisposeConnectionAsync(staleConnection, connectionCancellation.Token);
            }

            IConnection connection;
            try
            {
                connection = await _connectionFactory(connectionCancellation.Token);
            }
            catch (OperationCanceledException) when (
                _disposalCancellation.IsCancellationRequested &&
                !cancellationToken.IsCancellationRequested)
            {
                throw new ObjectDisposedException(nameof(RabbitMqConnectionManager));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                MessagingTelemetry.RecordConnectionEvent(
                    RabbitMqConnectionEvent.ConnectFailure,
                    TelemetryErrorCategory.Connection);
                RabbitMqLog.ConnectionFailed(_logger, ex);
                throw;
            }

            // A connection factory is allowed to ignore cancellation. Never publish a
            // connection that arrived after disposal began; abort it on the acquiring
            // operation so a timed-out DisposeAsync cannot leak a late connection.
            if (Volatile.Read(ref _disposed) != 0)
            {
                DisposeLateConnection(connection);
                throw new ObjectDisposedException(nameof(RabbitMqConnectionManager));
            }

            connection.ConnectionShutdownAsync += OnConnectionShutdownAsync;
            connection.CallbackExceptionAsync += OnCallbackExceptionAsync;
            connection.RecoverySucceededAsync += OnRecoverySucceededAsync;
            connection.ConnectionRecoveryErrorAsync += OnConnectionRecoveryErrorAsync;
            if (!TrySetCurrentConnection(connection))
            {
                await DisposeConnectionAsync(connection, connectionCancellation.Token);
                throw new ObjectDisposedException(nameof(RabbitMqConnectionManager));
            }

            MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.ConnectSuccess);
            RabbitMqLog.ConnectionEstablished(_logger, _options.Host, _options.Port, _options.VirtualHost);
            return connection;
        }
        finally
        {
            _connectionLock.Release();
        }
    }

    private Task<IConnection> CreateConnectionAsync(CancellationToken cancellationToken)
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

        return factory.CreateConnectionAsync(cancellationToken);
    }

    private Task OnConnectionShutdownAsync(object sender, ShutdownEventArgs args)
    {
        if (!TryMarkCurrentConnectionClosed(sender))
        {
            return Task.CompletedTask;
        }

        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.Shutdown);
        if (Volatile.Read(ref _disposed) == 0)
        {
            RabbitMqLog.ConnectionShutdown(_logger, args.Initiator, args.ReplyCode, args.ReplyText);
        }

        return Task.CompletedTask;
    }

    private Task OnCallbackExceptionAsync(object sender, CallbackExceptionEventArgs args)
    {
        if (!IsActiveCurrentConnection(sender))
        {
            return Task.CompletedTask;
        }

        RabbitMqLog.CallbackFailed(_logger, args.Exception);
        return Task.CompletedTask;
    }

    private Task OnRecoverySucceededAsync(object sender, AsyncEventArgs args)
    {
        if (!TryMarkCurrentConnectionOpen(sender))
        {
            return Task.CompletedTask;
        }

        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.RecoverySuccess);
        RabbitMqLog.RecoverySucceeded(_logger);
        return Task.CompletedTask;
    }

    private Task OnConnectionRecoveryErrorAsync(object sender, ConnectionRecoveryErrorEventArgs args)
    {
        if (!TryMarkCurrentConnectionClosed(sender))
        {
            return Task.CompletedTask;
        }

        MessagingTelemetry.RecordConnectionEvent(
            RabbitMqConnectionEvent.RecoveryFailure,
            TelemetryErrorCategory.Connection);
        RabbitMqLog.RecoveryFailed(_logger, args.Exception);
        return Task.CompletedTask;
    }

    private IConnection? TakeCurrentConnection()
    {
        lock (_connectionStateSync)
        {
            var connection = _connection;
            Volatile.Write(ref _connection, null);
            return connection;
        }
    }

    private bool TrySetCurrentConnection(IConnection connection)
    {
        lock (_connectionStateSync)
        {
            if (Volatile.Read(ref _disposed) != 0)
            {
                return false;
            }

            Volatile.Write(ref _connection, connection);
            MarkConnectionOpenCore();
            return true;
        }
    }

    private bool TryMarkCurrentConnectionOpen(object sender)
    {
        lock (_connectionStateSync)
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(sender, _connection))
            {
                return false;
            }

            MarkConnectionOpenCore();
            return true;
        }
    }

    private bool TryMarkCurrentConnectionClosed(object sender)
    {
        lock (_connectionStateSync)
        {
            if (!ReferenceEquals(sender, _connection))
            {
                return false;
            }

            MarkConnectionClosedCore();
            return true;
        }
    }

    private bool IsActiveCurrentConnection(object sender)
    {
        lock (_connectionStateSync)
        {
            return Volatile.Read(ref _disposed) == 0 && ReferenceEquals(sender, _connection);
        }
    }

    private void MarkConnectionClosed()
    {
        lock (_connectionStateSync)
        {
            MarkConnectionClosedCore();
        }
    }

    private void MarkConnectionOpenCore()
    {
        if (Interlocked.Exchange(ref _connectionCounted, 1) == 0)
        {
            MessagingTelemetry.AddConnection(1);
        }
    }

    private void MarkConnectionClosedCore()
    {
        if (Interlocked.Exchange(ref _connectionCounted, 0) == 1)
        {
            MessagingTelemetry.AddConnection(-1);
        }
    }

    private async Task DisposeConnectionAsync(IConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            await connection.CloseAsync(cancellationToken).WaitAsync(cancellationToken);
        }
        catch
        {
            // The connection is already unavailable.
        }
        finally
        {
            try
            {
                connection.ConnectionShutdownAsync -= OnConnectionShutdownAsync;
                connection.CallbackExceptionAsync -= OnCallbackExceptionAsync;
                connection.RecoverySucceededAsync -= OnRecoverySucceededAsync;
                connection.ConnectionRecoveryErrorAsync -= OnConnectionRecoveryErrorAsync;
            }
            finally
            {
                try
                {
                    connection.Dispose();
                }
                finally
                {
                    // A recovery callback can race the start of cleanup. This final
                    // transition happens after detachment and disposal, so it repairs
                    // any open transition that began before ownership was cleared.
                    MarkConnectionClosed();
                }
            }
        }
    }

    private static void DisposeLateConnection(IConnection connection)
    {
        try
        {
            connection.Dispose();
        }
        catch
        {
            // Best-effort abort of a connection returned after manager disposal.
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_disposalSync)
        {
            _disposalTask ??= DisposeCoreAsync();
            return new ValueTask(_disposalTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        _disposalCancellation.Cancel();

        using var timeout = new CancellationTokenSource(_disposalTimeout);
        var lockTaken = false;
        try
        {
            try
            {
                await _connectionLock.WaitAsync(timeout.Token);
                lockTaken = true;
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                // The in-progress acquisition owns the semaphore and is responsible for
                // disposing any connection that arrives late. Do not dispose the semaphore:
                // that owner and any pre-existing waiter must still be able to release it.
                RabbitMqLog.ConnectionDisposalTimedOut(_logger, _disposalTimeout.TotalSeconds);
                return;
            }

            var connection = TakeCurrentConnection();
            if (connection is not null)
            {
                await DisposeConnectionAsync(connection, timeout.Token);
            }
        }
        finally
        {
            if (lockTaken)
            {
                _connectionLock.Release();
            }

            // These synchronization objects intentionally remain undisposed. A call
            // that passed its initial disposal check may still be queued on the
            // semaphore, and a cancellation-ignoring acquisition may finish after
            // this bounded disposal task has returned.
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
