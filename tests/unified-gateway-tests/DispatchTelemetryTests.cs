using ImagingPipeline.PipelineCatalog;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;
using ImagingPipeline.UnifiedGateway.Dispatch;
using Microsoft.Extensions.Logging;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class DispatchTelemetryTests
{
    [Fact]
    public async Task DeliveredUnitHasOkSpanSuccessMetricAndStaticDebugLog()
    {
        var unit = Unit(RabbitMqPipeline("asd"), $"delivered-{Guid.NewGuid():N}");
        using var capture = new TelemetryCapture(unit.DispatchId);

        await Dispatcher(capture.Logger, unit => DispatchOutcome.Delivered(unit)).DispatchAsync([unit], CancellationToken.None);

        var span = Assert.Single(capture.Spans);
        Assert.Equal("unified_gateway.dispatch", span.OperationName);
        Assert.Equal(ActivityStatusCode.Ok, span.Status);
        Assert.Equal("asd", span.GetTagItem(TelemetryAttributeNames.PipelineId));
        Assert.Equal("rabbitmq", span.GetTagItem(TelemetryAttributeNames.PipelineTransport));
        Assert.Equal("success", span.GetTagItem(TelemetryAttributeNames.PipelineOutcome));
        Assert.Null(span.GetTagItem("error.type"));

        var metric = Assert.Single(capture.Units);
        Assert.Equal("success", metric[TelemetryAttributeNames.PipelineOutcome]);
        Assert.False(metric.ContainsKey("error.type"));

        var log = Assert.Single(capture.Logs);
        Assert.Equal(6001, log.EventId);
        Assert.Equal("Pipeline dispatch delivered.", log.Message);
        Assert.Equal(unit.DispatchId, log.Fields["messaging.message.id"]);
        Assert.Equal("source-1", log.Fields["messaging.message.conversation_id"]);
        Assert.Equal("asd", log.Fields[TelemetryAttributeNames.PipelineId]);
        Assert.Equal("rabbitmq", log.Fields[TelemetryAttributeNames.PipelineTransport]);
    }

    [Fact]
    public async Task FailedUnitHasErrorSpanCategoryMetricAndStaticWarningWithStructuredFields()
    {
        var unit = Unit(HttpPipeline("algo"), $"failed-{Guid.NewGuid():N}");
        var failure = new InvalidOperationException("endpoint said no");
        using var capture = new TelemetryCapture(unit.DispatchId);

        await Dispatcher(
                capture.Logger,
                unit => DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, failure, statusCode: 422))
            .DispatchAsync([unit], CancellationToken.None);

        var span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("rejected", span.GetTagItem(TelemetryAttributeNames.PipelineOutcome));
        Assert.Equal("validation", span.GetTagItem("error.type"));
        Assert.Equal("validation", span.GetTagItem(TelemetryAttributeNames.ErrorCategory));
        Assert.Equal(422, span.GetTagItem("http.response.status_code"));
        Assert.Empty(span.Events);

        var metric = Assert.Single(capture.Units);
        Assert.Equal("rejected", metric[TelemetryAttributeNames.PipelineOutcome]);
        Assert.Equal("validation", metric["error.type"]);
        Assert.Equal("algo", metric[TelemetryAttributeNames.PipelineId]);
        Assert.Equal("http", metric[TelemetryAttributeNames.PipelineTransport]);

        var log = Assert.Single(capture.Logs);
        Assert.Equal(6002, log.EventId);
        Assert.Equal(LogLevel.Warning, log.Level);
        Assert.Equal("Pipeline dispatch failed.", log.Message);
        Assert.Same(failure, log.Exception);
        Assert.Equal("rejected", log.Fields[TelemetryAttributeNames.PipelineOutcome]);
        Assert.Equal("validation", log.Fields[TelemetryAttributeNames.ErrorCategory]);
        Assert.Equal(422, log.Fields["StatusCode"]);
        Assert.Equal("algo", log.Fields[TelemetryAttributeNames.PipelineId]);
        Assert.Equal(unit.DispatchId, log.Fields["messaging.message.id"]);
    }

    [Fact]
    public async Task CancelledUnitHasCancelledSpanAndMetric()
    {
        var unit = Unit(RabbitMqPipeline("asd"), $"cancelled-{Guid.NewGuid():N}");
        using var capture = new TelemetryCapture(unit.DispatchId);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Dispatcher(
                capture.Logger,
                _ => throw new OperationCanceledException(cancellation.Token))
            .DispatchAsync([unit], cancellation.Token));

        var span = Assert.Single(capture.Spans);
        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("cancelled", span.GetTagItem(TelemetryAttributeNames.PipelineOutcome));
        Assert.Equal("cancelled", span.GetTagItem("error.type"));
        var metric = Assert.Single(capture.Units);
        Assert.Equal("cancelled", metric[TelemetryAttributeNames.PipelineOutcome]);
        Assert.Empty(capture.Logs);
    }

    [Fact]
    public async Task ListenerFailureLogsStaticWarningInsideTheDispatchScope()
    {
        var unit = Unit(RabbitMqPipeline("asd"), $"listener-{Guid.NewGuid():N}");
        using var capture = new TelemetryCapture(unit.DispatchId);
        var dispatcher = new PipelineDispatcher(
            [new DelegateTransport(PipelineTransportKind.RabbitMq, unit => DispatchOutcome.Delivered(unit))],
            [new FailingListener()],
            capture.Logger);

        await dispatcher.DispatchAsync([unit], CancellationToken.None);

        var log = Assert.Single(capture.Logs, entry => entry.EventId == 6003);
        Assert.Equal("Dispatch delivery listener failed; the unit remains delivered.", log.Message);
        Assert.Equal(nameof(FailingListener), log.Fields["Listener"]);
        Assert.Equal(unit.DispatchId, log.Fields["messaging.message.id"]);
        Assert.Equal("asd", log.Fields[TelemetryAttributeNames.PipelineId]);
    }

    private static PipelineDispatcher Dispatcher(
        ILogger<PipelineDispatcher> logger,
        Func<DispatchUnit, DispatchOutcome> result) =>
        new([new DelegateTransport(PipelineTransportKind.RabbitMq, result), new DelegateTransport(PipelineTransportKind.Http, result)], [], logger);

    private sealed class DelegateTransport(PipelineTransportKind kind, Func<DispatchUnit, DispatchOutcome> result) : IDispatchTransport
    {
        public PipelineTransportKind Kind => kind;

        public Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken) =>
            Task.FromResult(result(unit));
    }

    private sealed class FailingListener : IDispatchDeliveryListener
    {
        public ValueTask OnDeliveredAsync(DispatchUnit unit, CancellationToken cancellationToken) =>
            ValueTask.FromException(new InvalidOperationException("store down"));
    }

    private sealed record LogEntry(
        int EventId,
        LogLevel Level,
        string Message,
        Exception? Exception,
        Dictionary<string, object?> Fields);

    /// <summary>
    /// Captures spans, dispatch metrics and logs for one dispatch ID. Listeners are process-wide,
    /// so everything is filtered to that ID to stay isolated from concurrently running tests.
    /// </summary>
    private sealed class TelemetryCapture : IDisposable
    {
        private readonly ActivityListener _activities;
        private readonly MeterListener _meters = new();
        private readonly ConcurrentQueue<Activity> _spans = new();
        private readonly ConcurrentQueue<Dictionary<string, object?>> _units = new();

        public TelemetryCapture(string dispatchId)
        {
            _activities = new ActivityListener
            {
                ShouldListenTo = source => source.Name == TelemetrySourceNames.UnifiedGateway,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (Equals(activity.GetTagItem("messaging.message.id"), dispatchId))
                    {
                        _spans.Enqueue(activity);
                    }
                }
            };
            ActivitySource.AddActivityListener(_activities);

            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "unified_gateway.dispatch.units")
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            };
            _meters.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            {
                // Metrics carry no dispatch ID, so only measurements made under this test's span count.
                if (Equals(Activity.Current?.GetTagItem("messaging.message.id"), dispatchId))
                {
                    _units.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value));
                }
            });
            _meters.Start();
            Logger = new RecordingLogger(dispatchId);
        }

        public RecordingLogger Logger { get; }
        public IReadOnlyList<Activity> Spans => _spans.ToArray();
        public IReadOnlyList<Dictionary<string, object?>> Units => _units.ToArray();
        public IReadOnlyList<LogEntry> Logs => Logger.Entries;

        public void Dispose()
        {
            _activities.Dispose();
            _meters.Dispose();
        }
    }

    private sealed class RecordingLogger(string dispatchId) : ILogger<PipelineDispatcher>
    {
        private readonly AsyncLocal<ImmutableScopes?> _scopes = new();
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scopes.Value;
            _scopes.Value = new ImmutableScopes(state, previous);
            return new Restore(() => _scopes.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var scope = _scopes.Value; scope is not null; scope = scope.Parent)
            {
                if (scope.State is IEnumerable<KeyValuePair<string, object?>> pairs)
                {
                    foreach (var pair in pairs)
                    {
                        fields.TryAdd(pair.Key, pair.Value);
                    }
                }
            }

            if (Equals(fields.GetValueOrDefault("messaging.message.id"), dispatchId))
            {
                _entries.Enqueue(new LogEntry(eventId.Id, logLevel, formatter(state, exception), exception, fields));
            }
        }

        private sealed record ImmutableScopes(object State, ImmutableScopes? Parent);

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
