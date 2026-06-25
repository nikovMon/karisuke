using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.RabbitMqClient;

internal static class RabbitMqClientDiagnostics
{
    public const string ActivitySourceName = "ImagingPipeline.RabbitMqClient";
    public const string MeterName = "ImagingPipeline.RabbitMqClient";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PublishedMessages =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.published");

    public static readonly Counter<long> PublishFailures =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.publish_failures");

    public static readonly Histogram<double> PublishDurationMs =
        Meter.CreateHistogram<double>("imagingpipeline.rabbitmq.publish.duration", "ms");

    public static readonly Counter<long> ConsumedMessages =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.consumed");

    public static readonly Counter<long> HandlerFailures =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.handler_failures");

    public static readonly Histogram<double> ProcessingDurationMs =
        Meter.CreateHistogram<double>("imagingpipeline.rabbitmq.consumer.processing.duration", "ms");

    public static readonly Counter<long> AckedMessages =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.acked");

    public static readonly Counter<long> NackedMessages =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.nacked");

    public static readonly Counter<long> DeadLetteredMessages =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.messages.dead_lettered");

    public static readonly UpDownCounter<long> PublisherChannels =
        Meter.CreateUpDownCounter<long>("imagingpipeline.rabbitmq.publisher.channels");

    public static readonly Counter<long> ConnectionFailures =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.connection.failures");

    public static readonly Counter<long> ConnectionRecoveries =
        Meter.CreateCounter<long>("imagingpipeline.rabbitmq.connection.recoveries");

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
