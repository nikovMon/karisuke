using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Reflection;
using Elasticsearch.Net;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Nest;
using LoggingLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class ElasticsearchHealthProbeTests
{
    [Fact]
    public async Task InvalidPingReturnsFalseAndLogsWarning()
    {
        var connection = new InMemoryConnection([], statusCode: 503);
        var settings = new ConnectionSettings(
            new SingleNodeConnectionPool(new Uri("http://localhost:9200")),
            connection);
        var logger = new RecordingLogger<ElasticsearchHealthProbe>();
        var probe = new ElasticsearchHealthProbe(new ElasticClient(settings), logger);

        var isHealthy = await probe.IsHealthyAsync();

        Assert.False(isHealthy);
        var entry = Assert.Single(logger.Entries, item => item.Level == LoggingLogLevel.Warning);
        Assert.Equal(LoggingLogLevel.Warning, entry.Level);
        Assert.Contains("invalid response", entry.Message, StringComparison.Ordinal);
        Assert.Equal(503, entry.Properties["HttpStatusCode"]);
    }

    [Fact]
    public async Task RepeatedFailureLogsOnlyOnUnhealthyTransitionAndRecoveryResetsGate()
    {
        var logger = new RecordingLogger<ElasticsearchHealthProbe>();
        var probe = CreateProbe(logger, false, false, true, false);

        Assert.False(await probe.IsHealthyAsync());
        Assert.False(await probe.IsHealthyAsync());
        Assert.True(await probe.IsHealthyAsync());
        Assert.False(await probe.IsHealthyAsync());

        var warnings = logger.Entries.Where(entry => entry.Level == LoggingLogLevel.Warning).ToArray();
        Assert.Equal(2, warnings.Length);
        Assert.All(warnings, entry => Assert.Equal(5050, entry.EventId.Id));
    }

    [Fact]
    public async Task EveryProbeRecordsBoundedHealthOutcomeAndDuration()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = CreateHealthMeterListener(measurements);
        var probe = CreateProbe(
            new RecordingLogger<ElasticsearchHealthProbe>(),
            false,
            true,
            false);

        Assert.False(await probe.IsHealthyAsync());
        Assert.True(await probe.IsHealthyAsync());
        Assert.False(await probe.IsHealthyAsync());

        var operations = measurements
            .Where(measurement => measurement.InstrumentName == TelemetryMetricNames.RulesOperations)
            .ToArray();
        var durations = measurements
            .Where(measurement => measurement.InstrumentName == TelemetryMetricNames.RulesOperationDuration)
            .ToArray();
        Assert.Equal(3, operations.Length);
        Assert.Equal(3, durations.Length);
        Assert.All(operations.Concat(durations), measurement =>
        {
            Assert.Equal("health", measurement.Tags["imaging_pipeline.rules.operation"]);
            Assert.Contains(
                measurement.Tags[TelemetryAttributeNames.PipelineOutcome],
                new object?[] { "success", "failure" });
            Assert.Subset(
                new HashSet<string>(StringComparer.Ordinal)
                {
                    "imaging_pipeline.rules.operation",
                    TelemetryAttributeNames.PipelineOutcome,
                    "error.type"
                },
                measurement.Tags.Keys.ToHashSet(StringComparer.Ordinal));
        });
        Assert.Equal(
            2,
            operations.Count(measurement =>
                Equals(measurement.Tags[TelemetryAttributeNames.PipelineOutcome], "failure")));
        Assert.All(durations, measurement => Assert.True(measurement.Value >= 0));
    }

    [Fact]
    public async Task CancellationPropagatesWithoutLoggingAndRecordsCancelledHealthOutcome()
    {
        var measurements = new ConcurrentBag<Measurement>();
        using var listener = CreateHealthMeterListener(measurements);
        var logger = new RecordingLogger<ElasticsearchHealthProbe>();
        var probe = CreateCancellingProbe(logger);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            probe.IsHealthyAsync(cancellation.Token));

        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LoggingLogLevel.Warning);
        var duration = Assert.Single(
            measurements,
            measurement => measurement.InstrumentName == TelemetryMetricNames.RulesOperationDuration);
        Assert.Equal("health", duration.Tags["imaging_pipeline.rules.operation"]);
        Assert.Equal("cancelled", duration.Tags[TelemetryAttributeNames.PipelineOutcome]);
        Assert.Equal("cancelled", duration.Tags["error.type"]);
    }

    private static ElasticsearchHealthProbe CreateProbe(
        RecordingLogger<ElasticsearchHealthProbe> logger,
        params bool[] healthOutcomes)
    {
        var responses = new Queue<PingResponse>(healthOutcomes.Select(CreatePingResponse));
        var client = CreateProxy<IElasticClient>((method, _) => method.Name switch
        {
            nameof(IElasticClient.PingAsync) => Task.FromResult(responses.Dequeue()),
            _ => throw new NotSupportedException($"Unexpected IElasticClient member {method.Name}.")
        });
        return new ElasticsearchHealthProbe(client, logger);
    }

    private static ElasticsearchHealthProbe CreateCancellingProbe(
        RecordingLogger<ElasticsearchHealthProbe> logger)
    {
        var client = CreateProxy<IElasticClient>((method, arguments) => method.Name switch
        {
            nameof(IElasticClient.PingAsync) => Task.FromCanceled<PingResponse>(
                arguments is { Length: > 1 } && arguments[1] is CancellationToken token && token.IsCancellationRequested
                    ? token
                    : new CancellationToken(canceled: true)),
            _ => throw new NotSupportedException($"Unexpected IElasticClient member {method.Name}.")
        });
        return new ElasticsearchHealthProbe(client, logger);
    }

    private static PingResponse CreatePingResponse(bool isHealthy) => new TestPingResponse(isHealthy);

    private static T CreateProxy<T>(Func<MethodInfo, object?[]?, object?> handler)
        where T : class
    {
        var proxy = DispatchProxy.Create<T, TestDispatchProxy>();
        ((TestDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private static MeterListener CreateHealthMeterListener(ConcurrentBag<Measurement> measurements)
    {
        var listener = new MeterListener
        {
            InstrumentPublished = (instrument, meterListener) =>
            {
                if (instrument.Meter.Name == TelemetrySourceNames.RulesApi &&
                    instrument.Name is TelemetryMetricNames.RulesOperations or
                        TelemetryMetricNames.RulesOperationDuration)
                {
                    meterListener.EnableMeasurementEvents(instrument);
                }
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            AddHealthMeasurement(measurements, instrument.Name, value, tags));
        listener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
            AddHealthMeasurement(measurements, instrument.Name, value, tags));
        listener.Start();
        return listener;
    }

    private static void AddHealthMeasurement<T>(
        ConcurrentBag<Measurement> measurements,
        string instrumentName,
        T value,
        ReadOnlySpan<KeyValuePair<string, object?>> tags)
        where T : struct
    {
        var tagValues = ToDictionary(tags);
        if (Equals(tagValues.GetValueOrDefault("imaging_pipeline.rules.operation"), "health"))
        {
            measurements.Add(new Measurement(instrumentName, Convert.ToDouble(value), tagValues));
        }
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(
        ReadOnlySpan<KeyValuePair<string, object?>> tags) =>
        tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value, StringComparer.Ordinal);

    public class TestDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } =
            static (method, _) => throw new NotSupportedException($"Unexpected member {method.Name}.");

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) =>
            Handler(targetMethod ?? throw new InvalidOperationException("Proxy target method is unavailable."), args);
    }

    private sealed class TestPingResponse(bool isHealthy) : PingResponse
    {
        public override bool IsValid => isHealthy;
    }

    private sealed record Measurement(
        string InstrumentName,
        double Value,
        IReadOnlyDictionary<string, object?> Tags);
}
