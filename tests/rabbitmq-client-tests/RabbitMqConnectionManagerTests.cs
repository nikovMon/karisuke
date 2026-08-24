using System.Reflection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqConnectionManagerTests
{
    [Fact]
    public async Task DisposeAsyncIsBoundedAndCleansUpAConnectionThatArrivesLate()
    {
        var connectionRequested = new TaskCompletionSource<CancellationToken>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var connectionResult = new TaskCompletionSource<IConnection>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var manager = new RabbitMqConnectionManager(
            Options.Create(new RabbitMqClientOptions()),
            NullLogger<RabbitMqConnectionManager>.Instance,
            cancellationToken =>
            {
                connectionRequested.TrySetResult(cancellationToken);
                return connectionResult.Task;
            },
            TimeSpan.FromMilliseconds(25));

        var acquisition = manager.GetConnectionAsync();
        var disposalToken = await connectionRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));
        var waitingAcquisition = manager.GetConnectionAsync();

        var firstDisposal = manager.DisposeAsync().AsTask();
        var concurrentDisposal = manager.DisposeAsync().AsTask();

        Assert.Same(firstDisposal, concurrentDisposal);
        await firstDisposal.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(disposalToken.IsCancellationRequested);
        Assert.False(acquisition.IsCompleted);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await waitingAcquisition.WaitAsync(TimeSpan.FromSeconds(1)));

        var lateConnection = DispatchProxy.Create<IConnection, TestConnectionProxy>();
        var lateConnectionProxy = (TestConnectionProxy)(object)lateConnection;
        lateConnectionProxy.EventSender = lateConnection;
        connectionResult.SetResult(lateConnection);

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await acquisition);
        Assert.True(lateConnectionProxy.IsDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => manager.GetConnectionAsync());
    }

    [Fact]
    public async Task DisposeAsyncCancelsStaleConnectionCleanup()
    {
        var connection = DispatchProxy.Create<IConnection, TestConnectionProxy>();
        var connectionProxy = (TestConnectionProxy)(object)connection;
        connectionProxy.EventSender = connection;
        connectionProxy.IsOpen = true;
        connectionProxy.BlockClose = true;
        var factoryCalls = 0;
        var manager = new RabbitMqConnectionManager(
            Options.Create(new RabbitMqClientOptions()),
            NullLogger<RabbitMqConnectionManager>.Instance,
            cancellationToken =>
            {
                factoryCalls++;
                return factoryCalls == 1
                    ? Task.FromResult(connection)
                    : Task.FromCanceled<IConnection>(cancellationToken);
            },
            TimeSpan.FromSeconds(1));

        Assert.Same(connection, await manager.GetConnectionAsync());
        connectionProxy.IsOpen = false;
        var reacquisition = manager.GetConnectionAsync();
        await connectionProxy.CloseRequested.Task.WaitAsync(TimeSpan.FromSeconds(1));

        await manager.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await reacquisition);
        Assert.True(connectionProxy.IsDisposed);
        Assert.Equal(2, factoryCalls);
    }

    [Fact]
    public async Task RecoveryCallbackFromReplacedConnectionDoesNotReopenGauge()
    {
        var firstConnection = DispatchProxy.Create<IConnection, TestConnectionProxy>();
        var firstProxy = (TestConnectionProxy)(object)firstConnection;
        firstProxy.EventSender = firstConnection;
        firstProxy.IsOpen = true;
        var secondConnection = DispatchProxy.Create<IConnection, TestConnectionProxy>();
        var secondProxy = (TestConnectionProxy)(object)secondConnection;
        secondProxy.EventSender = secondConnection;
        secondProxy.IsOpen = true;
        var connections = new Queue<IConnection>([firstConnection, secondConnection]);
        var manager = new RabbitMqConnectionManager(
            Options.Create(new RabbitMqClientOptions()),
            NullLogger<RabbitMqConnectionManager>.Instance,
            _ => Task.FromResult(connections.Dequeue()),
            TimeSpan.FromSeconds(1));

        Assert.Same(firstConnection, await manager.GetConnectionAsync());
        firstProxy.IsOpen = false;
        Assert.Same(secondConnection, await manager.GetConnectionAsync());
        Assert.Equal(1, ReadConnectionGaugeState(manager));

        await firstProxy.RaiseCapturedRecoverySucceededAsync();

        Assert.Equal(1, ReadConnectionGaugeState(manager));
        await manager.DisposeAsync();
        Assert.Equal(0, ReadConnectionGaugeState(manager));
    }

    [Fact]
    public void InputSettingsFallBackToPrimaryWhenInputClusterIsNotConfigured()
    {
        var options = new RabbitMqClientOptions
        {
            Host = "primary-host",
            Port = 5672,
            Username = "primary-user",
            Password = "primary-pass",
            VirtualHost = "/primary"
        };

        var settings = RabbitMqConnectionSettings.Input(options);

        Assert.Equal("primary-host", settings.Host);
        Assert.Equal(5672, settings.Port);
        Assert.Equal("primary-user", settings.Username);
        Assert.Equal("primary-pass", settings.Password);
        Assert.Equal("/primary", settings.VirtualHost);
    }

    [Fact]
    public void InputSettingsUseInputClusterWhenConfigured()
    {
        var options = new RabbitMqClientOptions
        {
            Host = "primary-host",
            InputCluster = new RabbitMqRemoteClusterOptions
            {
                Host = "remote-host",
                Port = 5673,
                Username = "remote-user",
                Password = "remote-pass",
                VirtualHost = "/remote"
            }
        };

        var settings = RabbitMqConnectionSettings.Input(options);

        Assert.Equal("remote-host", settings.Host);
        Assert.Equal(5673, settings.Port);
        Assert.Equal("remote-user", settings.Username);
        Assert.Equal("remote-pass", settings.Password);
        Assert.Equal("/remote", settings.VirtualHost);
    }

    [Fact]
    public void PrimarySettingsIgnoreInputClusterEvenWhenConfigured()
    {
        var options = new RabbitMqClientOptions
        {
            Host = "primary-host",
            Port = 5672,
            InputCluster = new RabbitMqRemoteClusterOptions
            {
                Host = "remote-host",
                Port = 5673
            }
        };

        var settings = RabbitMqConnectionSettings.Primary(options);

        Assert.Equal("primary-host", settings.Host);
        Assert.Equal(5672, settings.Port);
    }

    private static int ReadConnectionGaugeState(RabbitMqConnectionManager manager) =>
        (int)(typeof(RabbitMqConnectionManager)
            .GetField("_connectionCounted", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(manager)
            ?? throw new InvalidOperationException("Connection gauge state field was not found."));

    public class TestConnectionProxy : DispatchProxy
    {
        private readonly TaskCompletionSource _closeRequested = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _closeResult = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        public bool IsOpen { get; set; }
        public bool BlockClose { get; set; }
        public object? EventSender { get; set; }
        public bool IsDisposed { get; private set; }
        public TaskCompletionSource CloseRequested => _closeRequested;
        private AsyncEventHandler<AsyncEventArgs>? RecoverySucceededHandler { get; set; }

        public Task RaiseCapturedRecoverySucceededAsync() =>
            RecoverySucceededHandler?.Invoke(
                EventSender ?? throw new InvalidOperationException("Event sender was not configured."),
                new AsyncEventArgs(CancellationToken.None))
            ?? throw new InvalidOperationException("Recovery handler was not registered.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var memberName = targetMethod?.Name;
            if (memberName == nameof(IDisposable.Dispose))
            {
                IsDisposed = true;
                return null;
            }

            if (memberName == "get_IsOpen")
            {
                return IsOpen;
            }

            if (memberName == nameof(IConnection.CloseAsync))
            {
                _closeRequested.TrySetResult();
                return BlockClose ? _closeResult.Task : Task.CompletedTask;
            }

            if (memberName == "add_RecoverySucceededAsync")
            {
                RecoverySucceededHandler = (AsyncEventHandler<AsyncEventArgs>)args![0]!;
                return null;
            }

            if (memberName is not null &&
                (memberName.StartsWith("add_", StringComparison.Ordinal) ||
                 memberName.StartsWith("remove_", StringComparison.Ordinal)))
            {
                return null;
            }

            throw new NotSupportedException($"Unexpected member {memberName}.");
        }
    }
}
