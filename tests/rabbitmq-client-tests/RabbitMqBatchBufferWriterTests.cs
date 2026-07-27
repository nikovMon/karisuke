using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Threading.Channels;
using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqBatchBufferWriterTests
{
    [Theory]
    [InlineData(false, true, "failure", "unknown")]
    [InlineData(true, false, "cancelled", "cancelled")]
    [InlineData(true, true, "cancelled", "cancelled")]
    public async Task UnbufferedDeliveryRecordsATerminalOutcomeAndBalancesInFlight(
        bool cancelWrite,
        bool closeBuffer,
        string expectedOutcome,
        string expectedError)
    {
        var destination = $"test.batch-buffer.{Guid.NewGuid():N}";
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = CreateListener(destination, measurements);
        using var cancellation = new CancellationTokenSource();
        Channel<int> channel;

        if (closeBuffer)
        {
            channel = Channel.CreateUnbounded<int>();
            channel.Writer.TryComplete(new InvalidOperationException("Buffer closed."));
        }
        else
        {
            channel = Channel.CreateBounded<int>(1);
            Assert.True(channel.Writer.TryWrite(1));
        }

        if (cancelWrite)
        {
            cancellation.Cancel();
        }

        var write = RabbitMqBatchBufferWriter.WriteAsync(
            channel.Writer,
            2,
            destination,
            TelemetryTiming.StartTimestamp(),
            cancellation.Token);

        var exception = await Record.ExceptionAsync(() => write.AsTask());
        Assert.NotNull(exception);

        var inFlight = measurements
            .Where(static measurement => measurement.Name == TelemetryMetricNames.MessagingInFlight)
            .Select(static measurement => measurement.Value)
            .ToArray();
        Assert.Equal([1d, -1d], inFlight);

        var processed = Assert.Single(
            measurements,
            static measurement => measurement.Name == TelemetryMetricNames.MessagingProcessDuration);
        Assert.Equal(expectedOutcome, processed.Tags[TelemetryAttributeNames.PipelineOutcome]);
        Assert.Equal(expectedError, processed.Tags["error.type"]);
    }

    [Fact]
    public async Task SuccessfulWriteTransfersInFlightOwnershipToBatchReader()
    {
        var destination = $"test.batch-buffer.{Guid.NewGuid():N}";
        var measurements = new ConcurrentQueue<Measurement>();
        using var listener = CreateListener(destination, measurements);
        var channel = Channel.CreateUnbounded<int>();
        var receivedAt = TelemetryTiming.StartTimestamp();

        await RabbitMqBatchBufferWriter.WriteAsync(
            channel.Writer,
            42,
            destination,
            receivedAt,
            CancellationToken.None);

        Assert.True(channel.Reader.TryRead(out var item));
        Assert.Equal(42, item);
        var inFlight = measurements
            .Where(static measurement => measurement.Name == TelemetryMetricNames.MessagingInFlight)
            .Select(static measurement => measurement.Value)
            .ToArray();
        Assert.Equal([1d], inFlight);
        Assert.DoesNotContain(
            measurements,
            static measurement => measurement.Name == TelemetryMetricNames.MessagingProcessDuration);

        // Complete the ownership lifecycle represented by the batch reader.
        MessagingTelemetry.RecordProcessed(
            destination,
            TelemetryTiming.ElapsedSeconds(receivedAt),
            TelemetryOutcome.Success);
        MessagingTelemetry.AddInFlight(destination, -1);

        var completedInFlight = measurements
            .Where(static measurement => measurement.Name == TelemetryMetricNames.MessagingInFlight)
            .Select(static measurement => measurement.Value)
            .ToArray();
        Assert.Equal([1d, -1d], completedInFlight);
        var processed = Assert.Single(
            measurements,
            static measurement => measurement.Name == TelemetryMetricNames.MessagingProcessDuration);
        Assert.Equal("success", processed.Tags[TelemetryAttributeNames.PipelineOutcome]);
        Assert.DoesNotContain("error.type", processed.Tags.Keys);
    }

    private static MeterListener CreateListener(
        string destination,
        ConcurrentQueue<Measurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetrySourceNames.RabbitMq &&
                    instrument.Name is TelemetryMetricNames.MessagingInFlight
                        or TelemetryMetricNames.MessagingProcessDuration)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            AddMeasurement(destination, measurements, instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            AddMeasurement(destination, measurements, instrument.Name, value, tags));
        listener.Start();
        return listener;
    }

    private static void AddMeasurement<T>(
        string destination,
        ConcurrentQueue<Measurement> measurements,
        string name,
        T value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var tagValues = tags.ToArray().ToDictionary(
            static tag => tag.Key,
            static tag => tag.Value,
            StringComparer.Ordinal);
        if (Equals(tagValues.GetValueOrDefault("messaging.destination.name"), destination))
        {
            measurements.Enqueue(new Measurement(name, Convert.ToDouble(value), tagValues));
        }
    }

    private sealed record Measurement(
        string Name,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
