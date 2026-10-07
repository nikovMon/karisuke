using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Telemetry for sending one unit: begin with the unit, complete with its outcome, dispose to
/// close the span. Owns the span, metrics, outcome log and log scope, so each dimension is added
/// in one place. A scope disposed without an outcome counts as cancelled, because cancellation is
/// the only exception a dispatch lets through.
/// </summary>
internal sealed class DispatchTelemetry : IDisposable
{
    private static readonly Counter<long> Units = TelemetryMeters.UnifiedGateway.CreateCounter<long>(
        "unified_gateway.dispatch.units", "{unit}", "Dispatch outcomes by pipeline and transport.");
    private static readonly Histogram<double> Duration = TelemetryMeters.UnifiedGateway.CreateHistogram<double>(
        "unified_gateway.dispatch.duration", "s", "Time spent sending one unit over its transport.");

    private readonly DispatchUnit _unit;
    private readonly ILogger _logger;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly IDisposable? _logScope;
    private readonly PipelineSpanScope _span;
    private bool _completed;

    private DispatchTelemetry(DispatchUnit unit, ILogger logger)
    {
        _unit = unit;
        _logger = logger;
        // The dispatch ID is the outgoing message ID; the source message ID joins it to the
        // consumed message, for example when that message is dead-lettered.
        _logScope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("messaging.message.id", unit.DispatchId),
            new("messaging.message.conversation_id", unit.SourceMessageId),
            new(TelemetryAttributeNames.PipelineId, unit.PipelineId),
            new(TelemetryAttributeNames.PipelineTransport, unit.TransportKind.Value())
        });
        _span = PipelineSpanScope.StartStage(PipelineStage.UnifiedGateway, "dispatch", default);
        _span.SetTag(TelemetryAttributeNames.PipelineId, unit.PipelineId);
        _span.SetTag(TelemetryAttributeNames.PipelineTransport, unit.TransportKind.Value());
        _span.SetTag("messaging.message.id", unit.DispatchId);
    }

    public static DispatchTelemetry Begin(DispatchUnit unit, ILogger logger) => new(unit, logger);

    public void Complete(DispatchOutcome outcome)
    {
        _completed = true;
        var telemetryOutcome = outcome.Status switch
        {
            DispatchStatus.Delivered => TelemetryOutcome.Success,
            DispatchStatus.Retryable => TelemetryOutcome.Retry,
            _ => TelemetryOutcome.Rejected
        };

        _span.Activity.SetTelemetryOutcome(telemetryOutcome);
        if (outcome.StatusCode is { } statusCode)
        {
            _span.SetTag("http.response.status_code", statusCode);
        }

        RecordMetrics(telemetryOutcome, outcome.Error);
        if (outcome.Status == DispatchStatus.Delivered)
        {
            _logger.DispatchDelivered();
            return;
        }

        // This log owns the exception, so the span does not record it again.
        _span.Failed(outcome.Error, outcome.Exception, recordException: false);
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.PipelineOutcome, telemetryOutcome.Value()),
                   // findair.error.category, not error.type: in logs error.type holds the exception class.
                   new(TelemetryAttributeNames.ErrorCategory, outcome.Error.Value()),
                   // Mapped to http.response.status_code in ECS logs.
                   new("StatusCode", outcome.StatusCode)
               }))
        {
            _logger.DispatchFailed(outcome.Exception);
        }
    }

    public void Dispose()
    {
        if (!_completed)
        {
            _span.Activity.SetTelemetryOutcome(TelemetryOutcome.Cancelled);
            _span.Cancelled();
            RecordMetrics(TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled);
        }

        _span.Dispose();
        _logScope?.Dispose();
    }

    private void RecordMetrics(TelemetryOutcome outcome, TelemetryErrorCategory error)
    {
        // Pipeline ID and transport kind come from the deployment catalog, so they are bounded.
        var tags = new TagList
        {
            { TelemetryAttributeNames.PipelineId, _unit.PipelineId },
            { TelemetryAttributeNames.PipelineTransport, _unit.TransportKind.Value() },
            { TelemetryAttributeNames.PipelineOutcome, outcome.Value() }
        };
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        Units.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
    }
}
