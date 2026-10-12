using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqSettlementTelemetryTests
{
    [Fact]
    public void SettledAckRecordsTheMessageOutcomeWithoutError()
    {
        var fields = RecordOne(telemetry => telemetry.Settled(), CancellationToken.None);

        Assert.Equal("retry", fields.GetValueOrDefault(TelemetryAttributeNames.PipelineOutcome));
        Assert.Null(fields.GetValueOrDefault("error.type"));
    }

    [Fact]
    public void UnsettledAckIsAFailure()
    {
        var fields = RecordOne(_ => { }, CancellationToken.None);

        Assert.Equal("failure", fields.GetValueOrDefault(TelemetryAttributeNames.PipelineOutcome));
    }

    [Fact]
    public void UnsettledAckAfterCancellationIsCancelled()
    {
        var fields = RecordOne(_ => { }, new CancellationToken(canceled: true));

        Assert.Equal("cancelled", fields.GetValueOrDefault(TelemetryAttributeNames.PipelineOutcome));
        Assert.Equal("cancelled", fields.GetValueOrDefault("error.type"));
    }

    // Returns the tags of the one settlement measurement made for a unique queue.
    private static Dictionary<string, object?> RecordOne(
        Action<RabbitMqSettlementTelemetry> settle,
        CancellationToken cancellationToken)
    {
        var queue = $"settlement-telemetry-{Guid.NewGuid():N}";
        var measurements = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == TelemetryMetricNames.MessagingSettlements)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var fields = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (Equals(fields.GetValueOrDefault("messaging.destination.name"), queue))
            {
                measurements.Enqueue(fields);
            }
        });
        listener.Start();

        using (var telemetry = RabbitMqSettlementTelemetry.Begin(
                   queue, MessagingOperation.Ack, TelemetryOutcome.Retry, cancellationToken))
        {
            settle(telemetry);
        }

        return Assert.Single(measurements);
    }
}
