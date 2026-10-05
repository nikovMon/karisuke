using System.Collections.Frozen;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Sends prepared units over their pipelines' transports, in parallel, and reports one outcome per
/// unit in input order. It never throws for a failed unit; deciding what a failure means for the
/// source message (retry, dead-letter) is the caller's job.
/// </summary>
public sealed class PipelineDispatcher
{
    private static readonly Counter<long> Units = TelemetryMeters.UnifiedGateway.CreateCounter<long>(
        "unified_gateway.dispatch.units", description: "Dispatch outcomes by pipeline and transport.");
    private static readonly Histogram<double> Duration = TelemetryMeters.UnifiedGateway.CreateHistogram<double>(
        "unified_gateway.dispatch.duration", "s", "Time spent sending one unit over its transport.");

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
        var started = Stopwatch.GetTimestamp();
        using var activity = TelemetrySources.UnifiedGateway.StartActivity("unified_gateway.dispatch");
        activity?.SetTag("pipeline.id", unit.PipelineId);
        activity?.SetTag("pipeline.transport", unit.TransportKind);
        activity?.SetTag("messaging.message.id", unit.DispatchId);

        var outcome = await SendAsync(unit, cancellationToken);
        activity?.SetTag("pipeline.outcome", Name(outcome.Status));
        if (outcome.Status != DispatchStatus.Delivered)
        {
            activity?.SetStatus(ActivityStatusCode.Error, outcome.Reason);
        }

        var tags = new TagList
        {
            { "pipeline.id", unit.PipelineId },
            { "pipeline.transport", unit.TransportKind },
            { "outcome", Name(outcome.Status) }
        };
        Units.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, tags);
        Log(outcome);

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
            return DispatchOutcome.Rejected(unit, $"No dispatch transport is registered for kind '{unit.TransportKind}'.");
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
            return DispatchOutcome.Retryable(unit, $"Transport threw {ex.GetType().Name}.", ex);
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
                _logger.LogWarning(
                    ex,
                    "Delivery listener {Listener} failed for dispatch {DispatchId} to pipeline {PipelineId}; the unit remains delivered.",
                    listener.GetType().Name, unit.DispatchId, unit.PipelineId);
            }
        }
    }

    private void Log(DispatchOutcome outcome)
    {
        var unit = outcome.Unit;
        if (outcome.Status == DispatchStatus.Delivered)
        {
            _logger.LogDebug(
                "Dispatch {DispatchId} delivered to pipeline {PipelineId} over {Transport}.",
                unit.DispatchId, unit.PipelineId, unit.TransportKind);
            return;
        }

        // Carries the source message ID so a dead-lettered source message can be joined to the
        // pipeline that caused it.
        _logger.LogWarning(
            outcome.Exception,
            "Dispatch {DispatchId} for source message {SourceMessageId} to pipeline {PipelineId} over {Transport} was {DispatchOutcome}: {Reason}",
            unit.DispatchId, unit.SourceMessageId, unit.PipelineId, unit.TransportKind, Name(outcome.Status), outcome.Reason);
    }

    private static string Name(DispatchStatus status) => status switch
    {
        DispatchStatus.Delivered => "delivered",
        DispatchStatus.Retryable => "retryable",
        _ => "rejected"
    };
}
