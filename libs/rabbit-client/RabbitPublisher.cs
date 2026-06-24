using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;
using System.Diagnostics;

namespace ImagingPipeline.RabbitClient;

internal sealed class RabbitPublisher : IRabbitPublisher
{
    private readonly IRabbitPublisherChannelPool _channels;
    private readonly RabbitClientOptions _options;
    private readonly ILogger<RabbitPublisher> _logger;

    public RabbitPublisher(
        IRabbitPublisherChannelPool channels,
        IOptions<RabbitClientOptions> options,
        ILogger<RabbitPublisher> logger)
    {
        _channels = channels;
        _options = options.Value;
        _logger = logger;
    }

    public Task PublishToInputAsync(RabbitMessageEnvelope message, CancellationToken cancellationToken = default) =>
        PublishAsync(_options.InputQueue, message, cancellationToken);

    public async Task PublishAsync(
        string queueName,
        RabbitMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(queueName))
        {
            throw new ArgumentException("Queue name must not be empty.", nameof(queueName));
        }

        using var activity = RabbitClientDiagnostics.ActivitySource.StartActivity("rabbitmq publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", queueName);
        activity?.SetTag("messaging.message.id", message.MessageId);
        activity?.SetTag("messaging.rabbitmq.routing_key", queueName);

        var started = Stopwatch.GetTimestamp();
        await using var lease = await _channels.LeaseAsync(cancellationToken);
        var channel = lease.Channel;
        await channel.QueueDeclareAsync(queueName, durable: true, exclusive: false, autoDelete: false,
            cancellationToken: cancellationToken);

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
                exchange: string.Empty,
                routingKey: queueName,
                mandatory: true,
                basicProperties: properties,
                body: message.Body,
                cancellationToken: cancellationToken);

            RabbitClientDiagnostics.PublishedMessages.Add(1, RabbitClientDiagnostics.Tag("queue", queueName));
            _logger.LogDebug("Published RabbitMQ message {MessageId} to {Queue}", message.MessageId, queueName);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            RabbitClientDiagnostics.PublishFailures.Add(1, RabbitClientDiagnostics.Tag("queue", queueName));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
        finally
        {
            RabbitClientDiagnostics.PublishDurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                RabbitClientDiagnostics.Tag("queue", queueName));
        }
    }
}
