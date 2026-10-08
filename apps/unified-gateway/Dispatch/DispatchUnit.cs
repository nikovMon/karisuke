using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.UnifiedGateway.Processing;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// One prepared payload bound for one pipeline. <see cref="DispatchId"/> is the downstream
/// idempotency key (AMQP MessageId, HTTP Idempotency-Key) and must be stable across redeliveries.
/// </summary>
public sealed record DispatchUnit(
    string DispatchId,
    PreparedPipelineWork Work,
    string? SourceMessageId = null,
    IReadOnlyDictionary<string, object?>? SourceHeaders = null)
{
    public string PipelineId => Work.Pipeline.PipelineId;
    // The catalog rejects a pipeline without a transport kind at startup.
    public PipelineTransportKind TransportKind => Work.Pipeline.Transport.Kind!.Value;
}

public enum DispatchStatus
{
    Delivered,
    /// <summary>Transient failure; the unit may succeed if sent again.</summary>
    Retryable,
    /// <summary>The destination refused the unit; sending it again will not help.</summary>
    Rejected
}

/// <summary>
/// The result of sending one unit. <see cref="Error"/> is a bounded failure category for logs,
/// spans and metrics; <see cref="StatusCode"/> is set when an HTTP endpoint answered.
/// </summary>
public sealed record DispatchOutcome(
    DispatchUnit Unit,
    DispatchStatus Status,
    TelemetryErrorCategory Error = TelemetryErrorCategory.None,
    int? StatusCode = null,
    Exception? Exception = null)
{
    public static DispatchOutcome Delivered(DispatchUnit unit, int? statusCode = null) =>
        new(unit, DispatchStatus.Delivered, StatusCode: statusCode);

    public static DispatchOutcome Retryable(
        DispatchUnit unit,
        TelemetryErrorCategory error,
        Exception? exception = null,
        int? statusCode = null) =>
        new(unit, DispatchStatus.Retryable, error, statusCode, exception);

    public static DispatchOutcome Rejected(
        DispatchUnit unit,
        TelemetryErrorCategory error,
        Exception? exception = null,
        int? statusCode = null) =>
        new(unit, DispatchStatus.Rejected, error, statusCode, exception);
}
