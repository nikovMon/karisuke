using System.Collections.Concurrent;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal interface IRabbitMqPublisherChannelPool : IAsyncDisposable
{
    ValueTask<RabbitMqPublisherChannelLease> LeaseAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// Marker for the channel pool that publishes to the input/retry side (PublishToInputAsync,
/// retry republish) when RabbitMqClientOptions.InputCluster is configured. Only registered
/// in DI when a gateway-style app actually configures InputCluster.
/// </summary>
internal interface IRabbitMqInputClusterChannelPool : IRabbitMqPublisherChannelPool
{
}

internal enum RabbitMqPublisherPoolRole
{
    /// <summary>Publishes OutputQueue on the primary/local cluster.</summary>
    Output,

    /// <summary>Publishes input/retry messages on the configured InputCluster.</summary>
    InputCluster
}

internal readonly struct RabbitMqPublisherChannelLease : IAsyncDisposable
{
    private readonly RabbitMqPublisherChannelPool _pool;

    public RabbitMqPublisherChannelLease(RabbitMqPublisherChannelPool pool, IChannel channel)
    {
        _pool = pool;
        Channel = channel;
    }

    public IChannel Channel { get; }

    public ValueTask DisposeAsync() => _pool.ReturnAsync(Channel);
}

internal class RabbitMqPublisherChannelPool : IRabbitMqPublisherChannelPool
{
    private readonly IRabbitMqConnectionManager _connections;
    private readonly RabbitMqClientOptions _options;
    private readonly RabbitMqPublisherPoolRole _role;
    private readonly ConcurrentQueue<IChannel> _channels = new();
    private readonly SemaphoreSlim _leases;
    private bool _disposed;

    public RabbitMqPublisherChannelPool(
        IRabbitMqPublisherConnectionManager connections,
        IOptions<RabbitMqClientOptions> options)
        : this((IRabbitMqConnectionManager)connections, options, RabbitMqPublisherPoolRole.Output)
    {
    }

    internal RabbitMqPublisherChannelPool(
        IRabbitMqConnectionManager connections,
        IOptions<RabbitMqClientOptions> options,
        RabbitMqPublisherPoolRole role)
    {
        _connections = connections;
        _options = options.Value;
        _role = role;
        _leases = new SemaphoreSlim(_options.PublisherChannelPoolSize, _options.PublisherChannelPoolSize);
    }

    public async ValueTask<RabbitMqPublisherChannelLease> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var waitStarted = TelemetryTiming.StartTimestamp();
        try
        {
            await _leases.WaitAsync(cancellationToken);
        }
        finally
        {
            MessagingTelemetry.RecordPublisherChannelWait(TelemetryTiming.ElapsedSeconds(waitStarted));
        }

        try
        {
            while (_channels.TryDequeue(out var channel))
            {
                if (channel.IsOpen)
                {
                    return new RabbitMqPublisherChannelLease(this, channel);
                }

                await DisposeChannelAsync(channel);
            }

            return new RabbitMqPublisherChannelLease(this, await CreateChannelAsync(cancellationToken));
        }
        catch
        {
            _leases.Release();
            throw;
        }
    }

    private async Task<IChannel> CreateChannelAsync(CancellationToken cancellationToken)
    {
        var connection = await _connections.GetConnectionAsync(cancellationToken);
        var channelOptions = new CreateChannelOptions(
            publisherConfirmationsEnabled: true,
            publisherConfirmationTrackingEnabled: true);
        var channel = await connection.CreateChannelAsync(channelOptions, cancellationToken);
        MessagingTelemetry.AddChannel(MessagingChannelRole.Publisher, 1);

        try
        {
            await DeclareTopologyAsync(channel, cancellationToken);
            return channel;
        }
        catch
        {
            await DisposeChannelAsync(channel);
            throw;
        }
    }

    /// <summary>
    /// The InputCluster pool always declares only the input/retry/DLQ side (it is on a
    /// separate broker from OutputQueue). The Output pool declares only the output side
    /// when InputCluster is configured, or the full combined topology otherwise, matching
    /// pre-split single-cluster behavior exactly.
    /// </summary>
    private Task DeclareTopologyAsync(IChannel channel, CancellationToken cancellationToken) =>
        _role switch
        {
            RabbitMqPublisherPoolRole.InputCluster => RabbitMqTopology.DeclareInputAsync(channel, _options, cancellationToken),
            _ when _options.InputCluster is not null => RabbitMqTopology.DeclareOutputAsync(channel, _options, cancellationToken),
            _ => RabbitMqTopology.DeclareAsync(channel, _options, cancellationToken)
        };

    internal async ValueTask ReturnAsync(IChannel channel)
    {
        try
        {
            if (!_disposed && channel.IsOpen)
            {
                _channels.Enqueue(channel);
                return;
            }

            await DisposeChannelAsync(channel);
        }
        finally
        {
            _leases.Release();
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
            // Broken channels are discarded and recreated on the next lease.
        }
        finally
        {
            MessagingTelemetry.AddChannel(MessagingChannelRole.Publisher, -1);
            channel.Dispose();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        while (_channels.TryDequeue(out var channel))
        {
            await DisposeChannelAsync(channel);
        }

        _leases.Dispose();
    }
}

internal sealed class RabbitMqInputClusterChannelPool : RabbitMqPublisherChannelPool, IRabbitMqInputClusterChannelPool
{
    public RabbitMqInputClusterChannelPool(
        IRabbitMqInputClusterConnectionManager connections,
        IOptions<RabbitMqClientOptions> options)
        : base(connections, options, RabbitMqPublisherPoolRole.InputCluster)
    {
    }
}
