using System.Collections.Frozen;
using ImagingPipeline.Observability;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Sends prepared units over their pipelines' transports, in parallel, and reports one outcome per
/// unit in input order. It never throws for a failed unit; deciding what a failure means for the
/// source message (retry, dead-letter) is the caller's job.
/// </summary>
public sealed class PipelineDispatcher
{
    private readonly FrozenDictionary<string, IDispatchTransport> _transports;
    private readonly IReadOnlyList<IDispatchDeliveryListener> _listeners;
    private readonly ILogger<PipelineDispatcher> _logger;

    public PipelineDispatcher(
        IEnumerable<IDispatchTransport> transports,
        IEnumerable<IDispatchDeliveryListener> listeners,
        ILogger<PipelineDispatcher> logger)
    {
        var byKind = new Dictionary<string, IDispatchTransport>(StringComparer.Ordinal);
        foreach (var transport in transports)
        {
            if (!byKind.TryAdd(transport.Kind, transport))
            {
                throw new InvalidOperationException($"More than one dispatch transport is registered for kind '{transport.Kind}'.");
            }
        }

        _transports = byKind.ToFrozenDictionary(StringComparer.Ordinal);
        _listeners = listeners.ToArray();
        _logger = logger;
    }

    public async Task<IReadOnlyList<DispatchOutcome>> DispatchAsync(
        IReadOnlyList<DispatchUnit> units,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(units);
        return units.Count switch
        {
            0 => [],
            1 => [await DispatchOneAsync(units[0], cancellationToken)],
            _ => await Task.WhenAll(units.Select(unit => DispatchOneAsync(unit, cancellationToken)))
        };
    }

    private async Task<DispatchOutcome> DispatchOneAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        // The span covers sending and listener notification, so listener logs carry its context.
        using var telemetry = DispatchTelemetry.Begin(unit, _logger);
        var outcome = await SendAsync(unit, cancellationToken);
        telemetry.Complete(outcome);

        if (outcome.Status == DispatchStatus.Delivered)
        {
            await NotifyDeliveredAsync(unit, cancellationToken);
        }

        return outcome;
    }

    private async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        if (!_transports.TryGetValue(unit.TransportKind, out var transport))
        {
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Handler);
        }

        try
        {
            return await transport.SendAsync(unit, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transports report failures as outcomes; an escaping exception is unexpected but must
            // not lose the other units' outcomes.
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Handler, ex);
        }
    }

    private async Task NotifyDeliveredAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        foreach (var listener in _listeners)
        {
            try
            {
                await listener.OnDeliveredAsync(unit, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                using (_logger.BeginScope(new KeyValuePair<string, object?>[] { new("Listener", listener.GetType().Name) }))
                {
                    _logger.DeliveryListenerFailed(ex);
                }
            }
        }
    }
}
