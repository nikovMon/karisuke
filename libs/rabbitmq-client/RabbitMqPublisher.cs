using ImagingPipeline.Observability;
using Microsoft.Extensions.Options;
using RabbitMQ.Client;

namespace ImagingPipeline.RabbitMqClient;

internal sealed class RabbitMqPublisher : IRabbitMqPublisher
{
    private readonly IRabbitMqPublisherChannelPool _channels;
    private readonly RabbitMqClientOptions _options;

    public RabbitMqPublisher(
        IRabbitMqPublisherChannelPool channels,
        IOptions<RabbitMqClientOptions> options)
    {
        _channels = channels;
        _options = options.Value;
    }

    public Task PublishToInputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            _options.InputExchange,
            _options.EffectiveInputRoutingKey,
            message,
            resetRetryCount: false,
            cancellationToken);

    public Task PublishToOutputAsync(RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default) =>
        PublishCoreAsync(
            _options.OutputExchange,
            _options.EffectiveOutputRoutingKey,
            message,
            resetRetryCount: true,
            cancellationToken);

    public Task PublishAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default) =>
        PublishCoreAsync(exchange, routingKey, message, resetRetryCount: false, cancellationToken);

    private async Task PublishCoreAsync(
        string exchange,
        string routingKey,
        RabbitMqMessageEnvelope message,
        bool resetRetryCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (string.IsNullOrWhiteSpace(routingKey))
        {
            throw new ArgumentException("Routing key must not be empty.", nameof(routingKey));
        }

        var destination = RabbitMqTelemetryDimensions.Destination(_options, exchange, routingKey);
        var started = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Success;
        var error = TelemetryErrorCategory.None;

        try
        {
            var headers = FilterOutboundHeaders(message.Headers, _options.RetryCountHeader);
            if (resetRetryCount)
            {
                ResetRetryCount(headers, _options.RetryCountHeader);
            }
            PipelineTimingHeaders.EnsureStarted(headers);
            headers[FindAirMessageHeaders.ContractVersion] =
                FindAirMessageHeaders.CurrentContractVersion;

            var properties = new BasicProperties
            {
                MessageId = message.MessageId,
                CorrelationId = message.CorrelationId,
                ContentType = message.ContentType,
                Persistent = true,
                Headers = headers
            };

            await using var lease = await _channels.LeaseAsync(cancellationToken);
            // This clock measures only the current broker hop. Native RabbitMQ tracing injects
            // trace context from the producer span during BasicPublishAsync.
            MessagingTimingHeaders.StampPublished(headers);
            await lease.Channel.BasicPublishAsync(
                exchange: exchange,
                routingKey: routingKey,
                mandatory: true,
                basicProperties: properties,
                body: message.Body,
                cancellationToken: cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            throw;
        }
        catch
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Publish;
            throw;
        }
        finally
        {
            MessagingTelemetry.RecordSent(
                destination,
                message.Body.LongLength,
                TelemetryTiming.ElapsedSeconds(started),
                outcome,
                error);
        }
    }

    internal static Dictionary<string, object?> FilterOutboundHeaders(
        IEnumerable<KeyValuePair<string, object?>>? source,
        string retryCountHeader)
    {
        var filtered = new Dictionary<string, object?>(StringComparer.Ordinal);
        if (source is null)
        {
            return filtered;
        }

        foreach (var header in source)
        {
            if (string.Equals(header.Key, FindAirMessageHeaders.TraceParent, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, FindAirMessageHeaders.TraceState, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, FindAirMessageHeaders.StartedAtUnixMilliseconds, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, FindAirMessageHeaders.AlgorithmName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, FindAirMessageHeaders.ContractVersion, StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, retryCountHeader, StringComparison.OrdinalIgnoreCase))
            {
                filtered[header.Key] = header.Value;
            }
        }

        return filtered;
    }
    internal static void ResetRetryCount(
        IDictionary<string, object?> headers,
        string retryCountHeader)
    {
        if (string.IsNullOrWhiteSpace(retryCountHeader))
        {
            return;
        }

        List<string>? variants = null;
        foreach (var key in headers.Keys)
        {
            if (!string.Equals(key, retryCountHeader, StringComparison.Ordinal)
                && string.Equals(key, retryCountHeader, StringComparison.OrdinalIgnoreCase))
            {
                (variants ??= []).Add(key);
            }
        }

        if (variants is not null)
        {
            foreach (var key in variants)
            {
                headers.Remove(key);
            }
        }

        headers[retryCountHeader] = 0;
    }
}

internal static class RabbitMqTelemetryDimensions
{
    public static string Destination(RabbitMqClientOptions options, string exchange, string routingKey)
    {
        if (Matches(exchange, routingKey, options.InputExchange, options.EffectiveInputRoutingKey))
        {
            return ActualDestination(exchange, routingKey);
        }

        if (Matches(exchange, routingKey, options.OutputExchange, options.EffectiveOutputRoutingKey))
        {
            return ActualDestination(exchange, routingKey);
        }

        if (Matches(exchange, routingKey, options.RetryExchange, options.EffectiveRetryRoutingKey))
        {
            return ActualDestination(exchange, routingKey);
        }

        if (Matches(exchange, routingKey, options.EffectiveDeadLetterExchange, options.EffectiveDeadLetterRoutingKey))
        {
            return ActualDestination(exchange, routingKey);
        }

        return "other";
    }

    private static bool Matches(
        string exchange,
        string routingKey,
        string configuredExchange,
        string configuredRoutingKey) =>
        string.Equals(exchange, configuredExchange, StringComparison.Ordinal) &&
        string.Equals(routingKey, configuredRoutingKey, StringComparison.Ordinal);

    private static string ActualDestination(string exchange, string routingKey) =>
        string.IsNullOrWhiteSpace(exchange) ? routingKey : exchange;
}
