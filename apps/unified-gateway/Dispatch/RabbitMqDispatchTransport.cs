using System.Collections.Frozen;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Publishes units to the RabbitMQ destination each pipeline declares in the catalog, on that
/// pipeline's named broker connection. Destinations are resolved once, at construction.
/// </summary>
public sealed class RabbitMqDispatchTransport : IDispatchTransport
{
    public const string TransportKind = "rabbitmq";

    private readonly IRabbitMqDestinationPublisher _publisher;
    private readonly FrozenDictionary<string, RabbitMqDestination> _destinations;

    public RabbitMqDispatchTransport(
        IPipelineCatalog catalog,
        IRabbitMqConnectionResolver connections,
        IRabbitMqDestinationPublisher publisher)
    {
        _publisher = publisher;
        _destinations = catalog.GetEnabled()
            .Where(pipeline => pipeline.Transport.Kind == TransportKind)
            .ToFrozenDictionary(
                pipeline => pipeline.PipelineId,
                pipeline => CreateDestination(pipeline.Transport.RabbitMq!, connections),
                StringComparer.Ordinal);
    }

    public string Kind => TransportKind;

    public async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_destinations.TryGetValue(unit.PipelineId, out var destination))
        {
            // Only an enabled pipeline whose catalog transport is rabbitmq has a destination.
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Handler);
        }

        try
        {
            await _publisher.PublishAsync(destination, CreateEnvelope(unit), cancellationToken);
            return DispatchOutcome.Delivered(unit);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Broker and connection failures are transient from the gateway's point of view;
            // the source message retry policy bounds how often they are attempted.
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Publish, ex);
        }
    }

    internal static RabbitMqMessageEnvelope CreateEnvelope(DispatchUnit unit)
    {
        var payload = unit.Work.Payload;
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        // Preserves the end-to-end pipeline clock. Trace context is injected from the current span.
        if (PipelineTimingHeaders.TryReadStartUnixMilliseconds(unit.SourceHeaders, out var startedAt))
        {
            headers[PipelineTimingHeaders.StartUnixMilliseconds] = startedAt;
        }

        foreach (var attribute in payload.Attributes)
        {
            headers[attribute.Key] = attribute.Value;
        }

        // Typed AMQP attributes win over string attributes with the same name.
        if (payload.RabbitMqAttributes is { } typed)
        {
            foreach (var attribute in typed)
            {
                headers[attribute.Key] = attribute.Value;
            }
        }

        return new RabbitMqMessageEnvelope(
            unit.DispatchId,
            payload.Body,
            payload.ContentType,
            headers,
            unit.SourceMessageId);
    }

    private static RabbitMqDestination CreateDestination(
        RabbitMqTransportOptions transport,
        IRabbitMqConnectionResolver connections)
    {
        var connection = connections.GetRequired(transport.ConnectionRef);
        var output = transport.Output;
        var exchange = output.ExchangeSettings;
        return new RabbitMqDestination
        {
            Connection = new RabbitMqDestinationConnection(
                transport.ConnectionRef,
                connection.Hostname,
                connection.Port,
                connection.Username,
                connection.Password,
                connection.VirtualHost),
            QueueName = output.QueueName,
            QueueArguments = output.Arguments,
            ExchangeName = exchange.ExchangeName,
            ExchangeType = exchange.ExchangeType,
            RoutingKey = output.GetEffectiveRoutingKey(),
            BindQueueToExchange = exchange.ShouldBindToExchange,
            ExchangeArguments = exchange.Arguments,
            BindingArguments = exchange.BindingArguments
        };
    }
}
