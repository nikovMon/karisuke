using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability.Tests;

public sealed class MessagingTelemetryTests
{
    [Fact]
    public void RetryCounterDoesNotUseExternalAttemptAsAMetricDimension()
    {
        KeyValuePair<string, object?>[]? capturedTags = null;
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == TelemetryMetricNames.MessagingRetries)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) => capturedTags = tags.ToArray());
        listener.Start();

        MessagingTelemetry.RecordRetry(
            "configured.input.queue",
            int.MaxValue,
            TelemetryOutcome.Exhausted,
            TelemetryErrorCategory.Handler);

        Assert.NotNull(capturedTags);
        Assert.DoesNotContain(
            capturedTags,
            static tag => tag.Key == TelemetryAttributeNames.RetryAttempt);
    }

    [Fact]
    public void ConnectionLifecycleCounterUsesOnlyBoundedEventAndErrorDimensions()
    {
        var measurements = new ConcurrentQueue<KeyValuePair<string, object?>[]>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Name == TelemetryMetricNames.RabbitMqConnectionEvents)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            measurements.Enqueue(tags.ToArray()));
        listener.Start();

        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.ConnectSuccess);
        MessagingTelemetry.RecordConnectionEvent(
            RabbitMqConnectionEvent.ConnectFailure,
            TelemetryErrorCategory.Connection);
        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.Shutdown);
        MessagingTelemetry.RecordConnectionEvent(RabbitMqConnectionEvent.RecoverySuccess);
        MessagingTelemetry.RecordConnectionEvent(
            RabbitMqConnectionEvent.RecoveryFailure,
            TelemetryErrorCategory.Connection);

        Assert.Equal(5, measurements.Count);
        var events = measurements
            .SelectMany(static tags => tags)
            .Where(static tag => tag.Key == "rabbitmq.connection.event")
            .Select(static tag => Assert.IsType<string>(tag.Value))
            .ToHashSet(StringComparer.Ordinal);
        Assert.Equal(
            ["connect_success", "connect_failure", "shutdown", "recovery_success", "recovery_failure"],
            events);
        Assert.All(
            measurements.SelectMany(static tags => tags),
            static tag => Assert.Contains(tag.Key, new[] { "rabbitmq.connection.event", "error.type" }));
    }
}
