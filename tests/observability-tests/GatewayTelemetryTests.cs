using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability.Tests;

public sealed class GatewayTelemetryTests
{
    [Fact]
    public void InvalidRuleGaugeTracksOnlyTheLastSuccessfullyPublishedSnapshot()
    {
        var measurements = new ConcurrentQueue<long>();
        using var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetrySourceNames.Gateway &&
                    instrument.Name == TelemetryMetricNames.GatewayRuleCacheSkippedRules)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, _, _) =>
            measurements.Enqueue(measurement));
        listener.Start();

        GatewayTelemetry.RecordCacheRefresh(
            durationSeconds: 0.01,
            outcome: TelemetryOutcome.Success,
            entries: 3,
            skippedRules: 2);
        listener.RecordObservableInstruments();

        Assert.Equal(2, Assert.Single(measurements));

        while (measurements.TryDequeue(out _))
        {
        }

        GatewayTelemetry.RecordCacheRefresh(
            durationSeconds: 0.01,
            outcome: TelemetryOutcome.Failure,
            entries: 99,
            error: TelemetryErrorCategory.Dependency,
            skippedRules: 99);
        listener.RecordObservableInstruments();

        Assert.Equal(2, Assert.Single(measurements));
    }
}
