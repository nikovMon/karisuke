using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Diagnostics;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly IRabbitMqPublisherChannelPool _channels;
    private readonly RabbitMqClientOptions _options;
    private readonly ILogger<RabbitMqPublisher> _logger;

    public RabbitMqPublisher(
        IRabbitMqPublisherChannelPool channels,
        IOptions<RabbitMqClientOptions> options,
        ILogger<RabbitMqPublisher> logger)
    {
        _channels = channels;
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync(_options.InputExchange, _options.EffectiveInputRoutingKey, message, cancellationToken);

    public Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync(_options.OutputExchange, _options.EffectiveOutputRoutingKey, message, cancellationToken);

    public async Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(routingKey))
        {
            throw new ArgumentException("Routing key must not be empty.", nameof(routingKey));
        }

        using var activity = RabbitMqClientDiagnostics.ActivitySource.StartActivity("rabbitmq publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", string.IsNullOrWhiteSpace(exchange) ? routingKey : exchange);
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.rabbitmq.exchange", exchange);
        activity?.SetTag("messaging.rabbitmq.routing_key", routingKey);

        var started = Stopwatch.GetTimestamp();
        await using var lease = await _channels.LeaseAsync(cancellationToken);
        var channel = lease.Channel;

        var properties = new BasicProperties
        {
            MessageId = message.MessageId,
            CorrelationId = message.CorrelationId,
            ContentType = message.ContentType,
            Persistent = true,
            Headers = message.Headers is null
                ? null
                : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal)
        };

        try
        {
            await channel.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: properties,
                body: message.Body,
                cancellationToken: cancellationToken);

            RabbitMqClientDiagnostics.PublishedMessages.Add(1,
                RabbitMqClientDiagnostics.Tag("exchange", exchange),
                RabbitMqClientDiagnostics.Tag("routing_key", routingKey));
            _logger.LogDebug("Published RabbitMQ message {MessageId} to {Exchange} with routing key {RoutingKey}",
                message.MessageId, exchange, routingKey);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitMqClientDiagnostics.PublishFailures.Add(1,
                RabbitMqClientDiagnostics.Tag("exchange", exchange),
                RabbitMqClientDiagnostics.Tag("routing_key", routingKey));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            RabbitMqClientDiagnostics.PublishDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                RabbitMqClientDiagnostics.Tag("exchange", exchange),
                RabbitMqClientDiagnostics.Tag("routing_key", routingKey));
        }
    }
}
