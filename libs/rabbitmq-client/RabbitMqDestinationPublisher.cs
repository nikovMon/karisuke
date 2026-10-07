using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqDestinationPublisher : IRabbitMqDestinationPublisher, IAsyncDisposable
{
    private readonly RabbitMqClientOptions _options;
    private readonly Func<RabbitMqDestinationConnection, IRabbitMqConnectionManager> _connectionManagerFactory;
    private readonly ConcurrentDictionary<RabbitMqDestinationConnection, Lazy<Route>> _routes = new();
    // A leased channel is used by one publish at a time, so each per-channel set needs no locking.
    private readonly ConditionalWeakTable<IChannel, HashSet<string>> _declaredDestinations = new();
    private int _disposed;

    public RabbitMqDestinationPublisher(
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqConnectionManager> connectionLogger)
        : this(
            options,
            connection => new RabbitMqDestinationConnectionManager(
                connection,
                options.Value.ReconnectDelaySeconds,
                connectionLogger))
    {
    }

    internal RabbitMqDestinationPublisher(
        IOptions<RabbitMqClientOptions> options,
        Func<RabbitMqDestinationConnection, IRabbitMqConnectionManager> connectionManagerFactory)
    {
        _options = options.Value;
        _connectionManagerFactory = connectionManagerFactory;
    }

    public async Task PublishAsync(
        RabbitMqDestination destination,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(message);
        destination.Validate();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

        var route = _routes.GetOrAdd(destination.Connection, CreateRoute).Value;
        using var telemetry = RabbitMqSendTelemetry.Begin(destination.TelemetryName, message.Body.LongLength, cancellationToken);

        // Headers belong to the caller's payload contract; only the shared timing headers are added.
        var headers = RabbitMqHeaders.Clone(message.Headers);
        PipelineTimingHeaders.EnsureStarted(headers);
        var properties = new BasicProperties
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            ContentType = message.ContentType,
            Persistent = true,
            Headers = headers
        };

        await using var lease = await route.Channels.LeaseAsync(cancellationToken);
        await EnsureDeclaredAsync(lease.Channel, destination, cancellationToken);
        MessagingTimingHeaders.StampPublished(headers);
        await lease.Channel.BasicPublishAsync(
            exchange: destination.ExchangeName,
            routingKey: destination.EffectiveRoutingKey,
            mandatory: true,
            basicProperties: properties,
            body: message.Body,
            cancellationToken: cancellationToken);
        telemetry.Succeeded();
    }

    private async Task EnsureDeclaredAsync(
        IChannel channel,
        RabbitMqDestination destination,
        CancellationToken cancellationToken)
    {
        var declared = _declaredDestinations.GetValue(channel, _ => new HashSet<string>(StringComparer.Ordinal));
        if (declared.Contains(destination.DeclarationKey))
        {
            return;
        }

        // A failed declaration closes the channel, and the pool discards closed channels on return.
        await RabbitMqTopology.DeclareDestinationAsync(channel, destination, cancellationToken);
        declared.Add(destination.DeclarationKey);
    }

    private Lazy<Route> CreateRoute(RabbitMqDestinationConnection connection) =>
        new(() =>
        {
            var connections = _connectionManagerFactory(connection);
            var channels = new RabbitMqPublisherChannelPool(
                connections,
                Options.Create(new RabbitMqClientOptions
                {
                    PublisherChannelPoolSize = _options.PublisherChannelPoolSize
                }),
                RabbitMqPublisherPoolRole.Destination);
            return new Route(connections, channels);
        });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var route in _routes.Values.Where(route => route.IsValueCreated).Select(route => route.Value))
        {
            await route.Channels.DisposeAsync();
            await route.Connections.DisposeAsync();
        }
    }

    private sealed record Route(IRabbitMqConnectionManager Connections, RabbitMqPublisherChannelPool Channels);
}
