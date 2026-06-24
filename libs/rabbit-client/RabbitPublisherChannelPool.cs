using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitClient;

internal interface IRabbitPublisherChannelPool : IAsyncDisposable
{
    ValueTask<RabbitPublisherChannelLease> LeaseAsync(CancellationToken cancellationToken = default);
}

internal readonly struct RabbitPublisherChannelLease : IAsyncDisposable
{
    private readonly RabbitPublisherChannelPool _pool;

    public RabbitPublisherChannelLease(RabbitPublisherChannelPool pool, IChannel channel)
    {
        _pool = pool;
        Channel = channel;
    }

    public IChannel Channel { get; }

    public ValueTask DisposeAsync() => _pool.ReturnAsync(Channel);
}

internal sealed class RabbitPublisherChannelPool : IRabbitPublisherChannelPool
{
    private readonly IRabbitConnectionManager _connections;
    private readonly RabbitClientOptions _options;
    private readonly ConcurrentQueue<IChannel> _channels = new();
    private readonly SemaphoreSlim _leases;
    private bool _disposed;

    public RabbitPublisherChannelPool(
        IRabbitConnectionManager connections,
        IOptions<RabbitClientOptions> options)
    {
        _connections = connections;
        _options = options.Value;
        _leases = new SemaphoreSlim(_options.PublisherChannelPoolSize, _options.PublisherChannelPoolSize);
    }

    public async ValueTask<RabbitPublisherChannelLease> LeaseAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _leases.WaitAsync(cancellationToken);

        try
        {
            while (_channels.TryDequeue(out var channel))
            {
                if (channel.IsOpen)
                {
                    return new RabbitPublisherChannelLease(this, channel);
                }

                await DisposeChannelAsync(channel);
            }

            return new RabbitPublisherChannelLease(this, await CreateChannelAsync(cancellationToken));
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
        RabbitClientDiagnostics.PublisherChannels.Add(1);
        return channel;
    }

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
            RabbitClientDiagnostics.PublisherChannels.Add(-1);
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
