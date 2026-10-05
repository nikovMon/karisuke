namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Sends a unit over one transport kind. Implementations report failures as outcomes rather than
/// throwing; only cancellation of <paramref name="cancellationToken"/> propagates.
/// </summary>
public interface IDispatchTransport
{
    /// <summary>The catalog <c>Transport.Kind</c> this transport handles.</summary>
    string Kind { get; }

    Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken);
}

/// <summary>
/// Notified after a unit is confirmed delivered, for example to record it as already processed.
/// A listener failure is logged and never changes the unit's outcome.
/// </summary>
public interface IDispatchDeliveryListener
{
    ValueTask OnDeliveredAsync(DispatchUnit unit, CancellationToken cancellationToken);
}
