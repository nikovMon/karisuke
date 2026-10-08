using ImagingPipeline.Observability;

namespace ImagingPipeline.RabbitMqClient;

/// <summary>
/// Records the send metrics for one publish: begin before publishing, call <see cref="Succeeded"/>
/// once the broker accepted the message, then dispose. Without success, the publish counts as
/// cancelled when the caller's token was cancelled, and as a failed publish otherwise.
/// </summary>
internal sealed class RabbitMqSendTelemetry : IDisposable
{
    private readonly string _destination;
    private readonly long _bodySizeBytes;
    private readonly CancellationToken _cancellationToken;
    private readonly long _started = TelemetryTiming.StartTimestamp();
    private bool _succeeded;

    private RabbitMqSendTelemetry(string destination, long bodySizeBytes, CancellationToken cancellationToken)
    {
        _destination = destination;
        _bodySizeBytes = bodySizeBytes;
        _cancellationToken = cancellationToken;
    }

    public static RabbitMqSendTelemetry Begin(string destination, long bodySizeBytes, CancellationToken cancellationToken) =>
        new(destination, bodySizeBytes, cancellationToken);

    public void Succeeded() => _succeeded = true;

    public void Dispose()
    {
        var (outcome, error) = _succeeded ? (TelemetryOutcome.Success, TelemetryErrorCategory.None)
            : _cancellationToken.IsCancellationRequested ? (TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled)
            : (TelemetryOutcome.Failure, TelemetryErrorCategory.Publish);
        MessagingTelemetry.RecordSent(_destination, _bodySizeBytes, TelemetryTiming.ElapsedSeconds(_started), outcome, error);
    }
}
