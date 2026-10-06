using System.Diagnostics;
using ImagingPipeline.Observability;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Telemetry for sending one unit: begin with the unit, complete with its outcome, dispose to
/// close the span. Owns the span, metrics, outcome log and log scope, so each dimension is added
/// in one place. A scope disposed without an outcome counts as cancelled, because cancellation is
/// the only exception a dispatch lets through.
/// </summary>
internal sealed class DispatchTelemetry : IDisposable
{
    private readonly DispatchUnit _unit;
    private readonly ILogger _logger;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly IDisposable _messageScope;
    private readonly IDisposable? _pipelineScope;
    private readonly PipelineSpanScope _span;
    private bool _completed;

    private DispatchTelemetry(DispatchUnit unit, ILogger logger)
    {
        _unit = unit;
        _logger = logger;
        // The dispatch ID is the outgoing message ID; the source message ID joins it to the
        // consumed message, for example when that message is dead-lettered.
        _messageScope = logger.BeginTelemetryScope(
            new TelemetryLogContext(MessageId: unit.DispatchId, CorrelationId: unit.SourceMessageId));
        _pipelineScope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("PipelineId", unit.PipelineId),
            new("Transport", unit.TransportKind)
        });
        _span = PipelineSpanScope.StartStage(PipelineStage.UnifiedGateway, "dispatch", default);
        _span.SetTag("pipeline.id", unit.PipelineId);
        _span.SetTag("pipeline.transport", unit.TransportKind);
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

        UnifiedGatewayTelemetry.RecordDispatch(
            _unit.PipelineId, _unit.TransportKind, telemetryOutcome, outcome.Error, ElapsedSeconds);

        if (outcome.Status == DispatchStatus.Delivered)
        {
            _logger.DispatchDelivered();
            return;
        }

        // This log owns the exception, so the span does not record it again.
        _span.Failed(outcome.Error, outcome.Exception, recordException: false);
        using (_logger.BeginScope(OutcomeFields(outcome)))
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
            UnifiedGatewayTelemetry.RecordDispatch(
                _unit.PipelineId, _unit.TransportKind, TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled, ElapsedSeconds);
        }

        _span.Dispose();
        _pipelineScope?.Dispose();
        _messageScope.Dispose();
    }

    private double ElapsedSeconds => Stopwatch.GetElapsedTime(_started).TotalSeconds;

    private static KeyValuePair<string, object?>[] OutcomeFields(DispatchOutcome outcome) =>
    [
        new("DispatchOutcome", outcome.Status == DispatchStatus.Retryable ? "retryable" : "rejected"),
        // Lower-case enum names match the bounded values on spans and metrics.
        new("ErrorType", outcome.Error.ToString().ToLowerInvariant()),
        // Mapped to http.response.status_code in ECS logs.
        new("StatusCode", outcome.StatusCode)
    ];
}
