using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class MessagingTelemetry
{
    private static readonly Counter<long> SentMessages = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.MessagingSent,
        "{message}",
        "Number of messages attempted to be sent to RabbitMQ.");

    private static readonly Counter<long> ConsumedMessages = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.MessagingConsumed,
        "{message}",
        "Number of RabbitMQ messages delivered to the application.");

    private static readonly Histogram<double> ClientDuration = TelemetryMeters.RabbitMq.CreateHistogram<double>(
        TelemetryMetricNames.MessagingClientDuration,
        "s",
        "Duration of a RabbitMQ client operation.");

    private static readonly Histogram<double> ProcessDuration = TelemetryMeters.RabbitMq.CreateHistogram<double>(
        TelemetryMetricNames.MessagingProcessDuration,
        "s",
        "Duration of processing a RabbitMQ delivery, including output publishing and settlement.");

    private static readonly Histogram<long> BodySize = TelemetryMeters.RabbitMq.CreateHistogram<long>(
        TelemetryMetricNames.MessagingBodySize,
        "By",
        "Size of a RabbitMQ message body.");

    private static readonly Histogram<double> DeliveryDelay = TelemetryMeters.RabbitMq.CreateHistogram<double>(
        TelemetryMetricNames.MessagingDeliveryDelay,
        "s",
        "Delay from message publication timestamp to application delivery.");

    private static readonly UpDownCounter<long> InFlight = TelemetryMeters.RabbitMq.CreateUpDownCounter<long>(
        TelemetryMetricNames.MessagingInFlight,
        "{message}",
        "Messages currently being processed by this service instance.");

    private static readonly Counter<long> Settlements = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.MessagingSettlements,
        "{message}",
        "RabbitMQ message settlement outcomes.");

    private static readonly Counter<long> Retries = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.MessagingRetries,
        "{message}",
        "RabbitMQ retry routing outcomes.");

    private static readonly UpDownCounter<long> Connections = TelemetryMeters.RabbitMq.CreateUpDownCounter<long>(
        TelemetryMetricNames.RabbitMqConnections,
        "{connection}",
        "Open RabbitMQ connections owned by this service instance.");

    private static readonly Counter<long> ConnectionEvents = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.RabbitMqConnectionEvents,
        "{event}",
        "RabbitMQ connection lifecycle events.");

    private static readonly UpDownCounter<long> Channels = TelemetryMeters.RabbitMq.CreateUpDownCounter<long>(
        TelemetryMetricNames.RabbitMqChannels,
        "{channel}",
        "Open RabbitMQ channels owned by this service instance.");

    private static readonly Histogram<double> ChannelWaitDuration = TelemetryMeters.RabbitMq.CreateHistogram<double>(
        TelemetryMetricNames.RabbitMqChannelWaitDuration,
        "s",
        "Time spent waiting to lease a RabbitMQ publisher channel.");

    private static readonly Counter<long> ConsumerRestarts = TelemetryMeters.RabbitMq.CreateCounter<long>(
        TelemetryMetricNames.RabbitMqConsumerRestarts,
        "{restart}",
        "RabbitMQ consumer restart attempts.");

    public static void RecordSent(
        string destination,
        string? routingKey,
        long bodySizeBytes,
        double durationSeconds,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = ClientTags(destination, MessagingOperation.Send, error);
        SentMessages.Add(1, tags);
        ClientDuration.Record(NonNegative(durationSeconds), tags);

        var sizeTags = DestinationTags(destination, MessagingOperation.Send);
        // Routing keys may be caller-controlled. Keep them on sampled spans, never metric dimensions.
        _ = routingKey;
        BodySize.Record(NonNegative(bodySizeBytes), sizeTags);

        if (outcome != TelemetryOutcome.Success)
        {
            // Outcome is intentionally not attached to the standard metric; error.type carries failure state.
            _ = outcome;
        }
    }

    public static void RecordConsumed(
        string destination,
        long bodySizeBytes,
        double? deliveryDelaySeconds = null,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = ClientTags(destination, MessagingOperation.Consume, error);
        ConsumedMessages.Add(1, tags);

        var bodyTags = DestinationTags(destination, MessagingOperation.Consume);
        BodySize.Record(NonNegative(bodySizeBytes), bodyTags);
        if (deliveryDelaySeconds is not null)
        {
            DeliveryDelay.Record(NonNegative(deliveryDelaySeconds.Value), bodyTags);
        }
    }

    public static void AddInFlight(string destination, int delta)
    {
        var tags = DestinationTags(destination, MessagingOperation.Process);
        InFlight.Add(delta, tags);
    }

    public static void RecordProcessed(
        string destination,
        double durationSeconds,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = ClientTags(destination, MessagingOperation.Process, error);
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        ProcessDuration.Record(NonNegative(durationSeconds), tags);
    }

    public static void RecordSettlement(
        string destination,
        MessagingOperation operation,
        TelemetryOutcome outcome,
        double durationSeconds,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        if (operation is not (MessagingOperation.Ack or MessagingOperation.Nack))
        {
            throw new ArgumentOutOfRangeException(nameof(operation), operation, "Settlement operation must be Ack or Nack.");
        }

        var tags = ClientTags(destination, operation, error);
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        Settlements.Add(1, tags);
        ClientDuration.Record(NonNegative(durationSeconds), tags);
    }

    public static void RecordRetry(
        string destination,
        int attempt,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = DestinationTags(destination, MessagingOperation.Process);
        // Retry headers are external input and may contain any integer. Keep the exact
        // attempt on sampled spans/logs, never as an unbounded metric dimension.
        _ = attempt;
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        AddError(ref tags, error);
        Retries.Add(1, tags);
    }

    public static void AddConnection(int delta) => Connections.Add(delta);

    public static void RecordConnectionEvent(
        RabbitMqConnectionEvent connectionEvent,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = new TagList
        {
            { "rabbitmq.connection.event", connectionEvent.Value() }
        };
        AddError(ref tags, error);
        ConnectionEvents.Add(1, tags);
    }

    public static void AddChannel(MessagingChannelRole role, int delta)
    {
        var tags = new TagList { { "messaging.channel.role", role.Value() } };
        Channels.Add(delta, tags);
    }

    public static void RecordPublisherChannelWait(double durationSeconds) =>
        ChannelWaitDuration.Record(NonNegative(durationSeconds));

    public static void RecordConsumerRestart(TelemetryErrorCategory error)
    {
        var tags = new TagList();
        AddError(ref tags, error);
        ConsumerRestarts.Add(1, tags);
    }

    private static TagList ClientTags(
        string destination,
        MessagingOperation operation,
        TelemetryErrorCategory error)
    {
        var tags = DestinationTags(destination, operation);
        AddError(ref tags, error);
        return tags;
    }

    private static TagList DestinationTags(string destination, MessagingOperation operation) => new()
    {
        { "messaging.system", "rabbitmq" },
        { "messaging.destination.name", RequiredDimension(destination, nameof(destination)) },
        { "messaging.operation.name", operation.Value() },
        { "messaging.operation.type", operation.OperationType() }
    };

    private static void AddError(ref TagList tags, TelemetryErrorCategory error)
    {
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }
    }

    private static string RequiredDimension(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }

    private static double NonNegative(double value) => value < 0 ? 0 : value;
    private static long NonNegative(long value) => value < 0 ? 0 : value;
}
