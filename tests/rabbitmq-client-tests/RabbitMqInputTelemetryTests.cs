using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqInputTelemetryTests
{
    [Fact]
    public void ForwardedFirstDeliveryIsExternalTransitWhileRetryIsRabbitMqDelivery()
    {
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name is TelemetryMetricNames.PipelineExternalStageDuration
                    or TelemetryMetricNames.MessagingDeliveryDelay)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            measurements.Enqueue(new Measurement(instrument.Name, value, tags.ToArray())));
        listener.Start();

        var options = new RabbitMqClientOptions
        {
            InputQueue = "int.algo.tile_builder.output",
            ForwardedInputStage = PipelineStage.TileBuilder
        };
        var firstDelivery = new RabbitMqDelivery(
            1,
            RabbitMqMessageEnvelope.FromUtf8("{}", "first"),
            Redelivered: false,
            PublishedToDeliverySeconds: 12.345);
        var retryDelivery = new RabbitMqDelivery(
            2,
            RabbitMqMessageEnvelope.FromUtf8("{}", "retry"),
            Redelivered: false,
            PublishedToDeliverySeconds: 6.789);
        var brokerRedelivery = new RabbitMqDelivery(
            3,
            RabbitMqMessageEnvelope.FromUtf8("{}", "redelivered"),
            Redelivered: true,
            PublishedToDeliverySeconds: 20.111);
        var forwardedRetryRedelivery = new RabbitMqDelivery(
            4,
            RabbitMqMessageEnvelope.FromUtf8("{}", "retry-redelivered"),
            Redelivered: true,
            PublishedToDeliverySeconds: 21.222);
        var ordinaryRedelivery = new RabbitMqDelivery(
            5,
            RabbitMqMessageEnvelope.FromUtf8("{}", "ordinary-redelivered"),
            Redelivered: true,
            PublishedToDeliverySeconds: 22.333);

        RabbitMqInputTelemetry.RecordConsumed(options, firstDelivery, retryAttempt: 0);
        RabbitMqInputTelemetry.RecordConsumed(options, retryDelivery, retryAttempt: 1);
        RabbitMqInputTelemetry.RecordConsumed(options, brokerRedelivery, retryAttempt: 0);
        RabbitMqInputTelemetry.RecordConsumed(options, forwardedRetryRedelivery, retryAttempt: 1);
        RabbitMqInputTelemetry.RecordConsumed(
            new RabbitMqClientOptions { InputQueue = "ordinary.input" },
            ordinaryRedelivery,
            retryAttempt: 0);

        Assert.Contains(
            measurements,
            measurement => measurement.Name == TelemetryMetricNames.PipelineExternalStageDuration
                           && measurement.Value == 12.345
                           && measurement.Tags.Any(tag =>
                               tag.Key == TelemetryAttributeNames.PipelineStage
                               && Equals(tag.Value, "tile_builder")));
        Assert.DoesNotContain(
            measurements,
            measurement => measurement.Name == TelemetryMetricNames.MessagingDeliveryDelay
                           && measurement.Value == 12.345);
        Assert.Contains(
            measurements,
            measurement => measurement.Name == TelemetryMetricNames.MessagingDeliveryDelay
                           && measurement.Value == 6.789);
        Assert.DoesNotContain(
            measurements,
            measurement => measurement.Name == TelemetryMetricNames.PipelineExternalStageDuration
                           && measurement.Value == 6.789);
        Assert.DoesNotContain(
            measurements,
            measurement => measurement.Value is 20.111 or 21.222 or 22.333);
    }

    private sealed record Measurement(
        string Name,
        double Value,
        KeyValuePair<string, object?>[] Tags);
}
