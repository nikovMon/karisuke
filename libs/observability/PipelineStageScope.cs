namespace ImagingPipeline.Observability;

/// <summary>
/// Owns the per-message pipeline metrics for one stage so message handlers do not have to
/// thread outcome, error category, and counters through their own control flow.
/// </summary>
/// <remarks>
/// <para>
/// The scope starts pessimistic: until a handler classifies the message it is reported as
/// <see cref="TelemetryOutcome.Failure"/> with error <see cref="TelemetryErrorCategory.Unknown"/>.
/// Disposing does not reinterpret that default, so a handler must classify every path it returns
/// from, and call <see cref="Faulted"/> from a catch-all to convert an unexpected exception into a
/// retryable handler failure. Reaching disposal unclassified means a path was missed.
/// </para>
/// <para>
/// Classification is first-wins: the innermost handler to classify a message decides its outcome,
/// so a specific cause such as a failed dependency survives the catch-all on the way out.
/// </para>
/// <para>
/// Disposing records the ingress message, the stage duration, and — when the handler published
/// anything — the egress messages and fan-out.
/// </para>
/// </remarks>
public sealed class PipelineStageScope : IDisposable
{
    private readonly PipelineStage _stage;
    private readonly long _startTimestamp;
    private TelemetryOutcome _outcome = TelemetryOutcome.Failure;
    private TelemetryErrorCategory _error = TelemetryErrorCategory.Unknown;
    private bool _classified;
    private bool _disposed;

    private PipelineStageScope(PipelineStage stage, long ingressPayloadBytes)
    {
        _stage = stage;
        _startTimestamp = TelemetryTiming.StartTimestamp();
        PipelineTelemetry.RecordPayloadSize(stage, PipelineDirection.Ingress, ingressPayloadBytes);
    }

    /// <summary>Messages the handler successfully published while inside this scope.</summary>
    public int PublishedCount { get; private set; }

    public static PipelineStageScope Begin(PipelineStage stage, long ingressPayloadBytes) =>
        new(stage, ingressPayloadBytes);

    /// <summary>The message is malformed or invalid and must not be retried.</summary>
    public void Rejected(TelemetryErrorCategory error) => Classify(TelemetryOutcome.Rejected, error);

    /// <summary>The message failed for a reason that another delivery may resolve.</summary>
    public void Retryable(TelemetryErrorCategory error) => Classify(TelemetryOutcome.Retry, error);

    public void Cancelled() => Classify(TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled);

    public void Succeeded() => Classify(TelemetryOutcome.Success, TelemetryErrorCategory.None);

    /// <summary>
    /// Classifies an unexpected exception as a retryable handler failure, unless the message was
    /// already classified. Returns <c>true</c> only when it applied, so the caller can attach the
    /// matching error to its span exactly once and nested catch blocks stay idempotent.
    /// </summary>
    public bool Faulted()
    {
        if (_classified)
        {
            return false;
        }

        Classify(TelemetryOutcome.Retry, TelemetryErrorCategory.Handler);
        return true;
    }

    public void MessagePublished() => PublishedCount++;

    public void RecordBatchSize(PipelineItem item, long count) =>
        PipelineTelemetry.RecordBatchSize(_stage, item, count);

    public void RecordEgressPayloadSize(long bytes) =>
        PipelineTelemetry.RecordPayloadSize(_stage, PipelineDirection.Egress, bytes);

    public void RecordEndToEndDuration(double durationSeconds) =>
        PipelineTelemetry.RecordEndToEndDuration(_stage, durationSeconds);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (PublishedCount > 0)
        {
            PipelineTelemetry.RecordMessage(
                _stage,
                PipelineDirection.Egress,
                TelemetryOutcome.Success,
                count: PublishedCount);
        }

        if (_outcome == TelemetryOutcome.Success)
        {
            PipelineTelemetry.RecordFanOut(_stage, PublishedCount);
        }

        PipelineTelemetry.RecordMessage(_stage, PipelineDirection.Ingress, _outcome, _error);
        PipelineTelemetry.RecordStageDuration(
            _stage,
            TelemetryTiming.ElapsedSeconds(_startTimestamp),
            _outcome,
            _error);
    }

    private void Classify(TelemetryOutcome outcome, TelemetryErrorCategory error)
    {
        _outcome = outcome;
        _error = error;
        _classified = true;
    }
}
