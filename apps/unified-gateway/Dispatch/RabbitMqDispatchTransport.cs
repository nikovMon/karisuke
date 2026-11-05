using System.Collections.Frozen;
using System.Net.Sockets;
using System.Security.Authentication;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqClient;
using RabbitMQ.Client.Exceptions;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Publishes units to the RabbitMQ destination each pipeline declares in the catalog, on that
/// pipeline's named broker connection. Destinations are resolved once, at construction.
/// </summary>
public sealed class RabbitMqDispatchTransport : IDispatchTransport
{

    private readonly IRabbitMqDestinationPublisher _publisher;
    private readonly FrozenDictionary<string, RabbitMqDestination> _destinations;

    public RabbitMqDispatchTransport(
        IPipelineCatalog catalog,
        IRabbitMqConnectionResolver connections,
        IRabbitMqDestinationPublisher publisher)
    {
        _publisher = publisher;
        _destinations = catalog.GetEnabled()
            .Where(pipeline => pipeline.Transport.Kind == PipelineTransportKind.RabbitMq)
            .ToFrozenDictionary(
                pipeline => pipeline.PipelineId,
                pipeline => CreateDestination(pipeline.Transport.RabbitMq!, connections),
                StringComparer.Ordinal);
    }

    public PipelineTransportKind Kind => PipelineTransportKind.RabbitMq;

    public async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_destinations.TryGetValue(unit.PipelineId, out var destination))
        {
            // Only an enabled pipeline whose catalog transport is rabbitmq has a destination.
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Handler, failureDetails:
                new(RabbitMqFailureKind.Configuration, "gateway.rabbitmq.destination_missing", "No enabled RabbitMQ destination is configured.",
                    unit.PipelineId, "dispatch_rabbitmq"));
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
            // Keep the existing short retry behavior, but do not authorize later replay for
            // authentication, topology mistakes or an unrecognized exception.
            var kind = ClassifyFailure(ex);
            var (code, message) = kind switch
            {
                RabbitMqFailureKind.Transient => ("gateway.rabbitmq.connection", "RabbitMQ publication failed transiently."),
                RabbitMqFailureKind.Configuration => ("gateway.rabbitmq.configuration", "RabbitMQ authentication, routing or topology configuration failed."),
                _ => ("gateway.rabbitmq.unclassified", "RabbitMQ publication failed without a recognized cause.")
            };
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Publish, ex, failureDetails:
                new(kind, code, message, unit.PipelineId, "dispatch_rabbitmq"));
        }
    }

    private static RabbitMqFailureKind ClassifyFailure(Exception exception)
    {
        if (exception is AuthenticationFailureException or PossibleAuthenticationFailureException or AuthenticationException
            or PublishException { IsReturn: true })
        {
            return RabbitMqFailureKind.Configuration;
        }

        if (exception is OperationInterruptedException interrupted)
        {
            return interrupted.ShutdownReason?.ReplyCode switch
            {
                403 or 404 or 405 or 406 or 530 => RabbitMqFailureKind.Configuration,
                320 or 541 => RabbitMqFailureKind.Transient,
                _ => RabbitMqFailureKind.Unknown
            };
        }

        if (exception is AggregateException aggregate)
        {
            var kinds = aggregate.InnerExceptions.Select(ClassifyFailure).ToArray();
            return kinds.Contains(RabbitMqFailureKind.Configuration) ? RabbitMqFailureKind.Configuration
                : kinds.Length > 0 && kinds.All(kind => kind == RabbitMqFailureKind.Transient) ? RabbitMqFailureKind.Transient
                : RabbitMqFailureKind.Unknown;
        }

        if (exception.InnerException is { } inner)
        {
            return ClassifyFailure(inner);
        }

        return exception is IOException or SocketException or TimeoutException or OperationCanceledException or PublishException { IsReturn: false }
            ? RabbitMqFailureKind.Transient
            : RabbitMqFailureKind.Unknown;
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
                transport.VirtualHost ?? connection.VirtualHost),
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
