using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// Records the settlement metric for one ack or nack: begin before settling, call
/// <see cref="Settled"/> once the broker call returned, then dispose. Without it, the settlement
/// counts as cancelled when the caller's token was cancelled, and as failed otherwise.
/// </summary>
internal sealed class RabbitMqSettlementTelemetry : IDisposable
{
    private readonly string _queue;
    private readonly MessagingOperation _operation;
    private readonly TelemetryOutcome _outcome;
    private readonly CancellationToken _cancellationToken;
    private readonly long _started = TelemetryTiming.StartTimestamp();
    private bool _settled;

    private RabbitMqSettlementTelemetry(
        string queue,
        MessagingOperation operation,
        TelemetryOutcome outcome,
        CancellationToken cancellationToken)
    {
        _queue = queue;
        _operation = operation;
        _outcome = outcome;
        _cancellationToken = cancellationToken;
    }

    /// <param name="outcome">What the settlement means for the message, such as retry or dead-letter.</param>
    public static RabbitMqSettlementTelemetry Begin(
        string queue,
        MessagingOperation operation,
        TelemetryOutcome outcome,
        CancellationToken cancellationToken) =>
        new(queue, operation, outcome, cancellationToken);

    public void Settled() => _settled = true;

    public void Dispose()
    {
        var (outcome, error) = _settled ? (_outcome, TelemetryErrorCategory.None)
            : _cancellationToken.IsCancellationRequested ? (TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled)
            : (TelemetryOutcome.Failure, TelemetryErrorCategory.Unknown);
        MessagingTelemetry.RecordSettlement(_queue, _operation, outcome, TelemetryTiming.ElapsedSeconds(_started), error);
    }
}
