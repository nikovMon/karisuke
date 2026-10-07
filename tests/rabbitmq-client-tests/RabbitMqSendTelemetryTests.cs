using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqSendTelemetryTests
{
    [Fact]
    public void SucceededPublishHasNoErrorType()
    {
        var errorType = RecordOne(telemetry => telemetry.Succeeded(), CancellationToken.None);

        Assert.Null(errorType);
    }

    [Fact]
    public void PublishWithoutSuccessIsAPublishFailure()
    {
        var errorType = RecordOne(_ => { }, CancellationToken.None);

        Assert.Equal("publish", errorType);
    }

    [Fact]
    public void PublishWithoutSuccessAfterCancellationIsCancelled()
    {
        var errorType = RecordOne(_ => { }, new CancellationToken(canceled: true));

        Assert.Equal("cancelled", errorType);
    }

    // Returns the error.type tag of the one sent-message measurement made for a unique destination.
    private static string? RecordOne(Action<RabbitMqSendTelemetry> publish, CancellationToken cancellationToken)
    {
        var destination = $"send-telemetry-{Guid.NewGuid():N}";
        var measurements = new ConcurrentQueue<Dictionary<string, object?>>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Name == TelemetryMetricNames.MessagingSent)
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var fields = tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value);
            if (Equals(fields.GetValueOrDefault("messaging.destination.name"), destination))
            {
                measurements.Enqueue(fields);
            }
        });
        listener.Start();

        using (var telemetry = RabbitMqSendTelemetry.Begin(destination, 10, cancellationToken))
        {
            publish(telemetry);
        }

        return Assert.Single(measurements).GetValueOrDefault("error.type") as string;
    }
}
