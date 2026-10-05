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
    public string TransportKind => Work.Pipeline.Transport.Kind;
}

public enum DispatchStatus
{
    Delivered,
    /// <summary>Transient failure; the unit may succeed if sent again.</summary>
    Retryable,
    /// <summary>The destination refused the unit; sending it again will not help.</summary>
    Rejected
}

public sealed record DispatchOutcome(
    DispatchUnit Unit,
    DispatchStatus Status,
    string? Reason = null,
    Exception? Exception = null)
{
    public static DispatchOutcome Delivered(DispatchUnit unit) => new(unit, DispatchStatus.Delivered);

    public static DispatchOutcome Retryable(DispatchUnit unit, string reason, Exception? exception = null) =>
        new(unit, DispatchStatus.Retryable, reason, exception);

    public static DispatchOutcome Rejected(DispatchUnit unit, string reason, Exception? exception = null) =>
        new(unit, DispatchStatus.Rejected, reason, exception);
}
