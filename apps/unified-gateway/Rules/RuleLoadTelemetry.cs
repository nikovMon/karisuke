using System.Diagnostics;
using System.Diagnostics.Metrics;
using ImagingPipeline.Observability;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.UnifiedGateway.Rules;

/// <summary>
/// Telemetry for loading one pipeline's rules: begin, report the built snapshot or the failure,
/// dispose. Owns the log scope, the outcome logs and the load metrics, so loading code stays
/// free of telemetry. A load disposed without an outcome counts as cancelled.
/// </summary>
internal sealed class RuleLoadTelemetry : IDisposable
{
    // Enough to diagnose a bad load without flooding the logs when many rules fail the same way.
    private const int MaxLoggedRejections = 10;

    private static readonly Counter<long> Loads = TelemetryMeters.UnifiedGateway.CreateCounter<long>(
        "unified_gateway.rules.loads", "{load}", "Rule loads by pipeline and outcome.");
    private static readonly Histogram<double> Duration = TelemetryMeters.UnifiedGateway.CreateHistogram<double>(
        "unified_gateway.rules.load.duration", "s", "Time spent loading and preparing one pipeline's rules.");

    private readonly string _pipelineId;
    private readonly RuleLoadKind _kind;
    private readonly ILogger _logger;
    private readonly IDisposable? _logScope;
    private readonly long _started = Stopwatch.GetTimestamp();
    private TelemetryOutcome? _outcome;

    private RuleLoadTelemetry(string pipelineId, RuleLoadKind kind, ILogger logger)
    {
        _pipelineId = pipelineId;
        _kind = kind;
        _logger = logger;
        _logScope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new(TelemetryAttributeNames.PipelineId, pipelineId)
        });
    }

    public static RuleLoadTelemetry Begin(string pipelineId, RuleLoadKind kind, ILogger logger) =>
        new(pipelineId, kind, logger);

    /// <summary>Logs the rejected rules, then the load's counts.</summary>
    public void Built(RuleSnapshot snapshot)
    {
        foreach (var rejection in snapshot.Rejections.Take(MaxLoggedRejections))
        {
            using (_logger.BeginScope(new KeyValuePair<string, object?>[]
                   {
                       new(TelemetryAttributeNames.PipelineRuleId, rejection.RuleId),
                       new(TelemetryAttributeNames.RuleRejectionReason, rejection.Reason)
                   }))
            {
                _logger.PipelineRuleRejected(rejection.Exception);
            }
        }

        if (snapshot.AllRulesRejected)
        {
            return;
        }

        _outcome = TelemetryOutcome.Success;
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RulesLoadedCount, snapshot.Rules.Count),
                   new(TelemetryAttributeNames.RulesRejectedCount, snapshot.Rejections.Count)
               }))
        {
            if (_kind == RuleLoadKind.Startup)
            {
                _logger.PipelineRulesLoaded();
            }
            else
            {
                _logger.PipelineRulesRefreshed();
            }
        }
    }

    /// <summary>Logs a failed load. The pipeline keeps the rules it had, if any.</summary>
    public void Failed(Exception exception, int retainedRuleCount)
    {
        _outcome = TelemetryOutcome.Failure;
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RulesRetainedCount, retainedRuleCount)
               }))
        {
            _logger.PipelineRulesLoadFailed(exception);
        }
    }

    public void Dispose()
    {
        // Pipeline IDs come from the deployment catalog, so they are bounded.
        var tags = new TagList
        {
            { TelemetryAttributeNames.PipelineId, _pipelineId },
            { TelemetryAttributeNames.PipelineOutcome, (_outcome ?? TelemetryOutcome.Cancelled).Value() }
        };
        Loads.Add(1, tags);
        Duration.Record(Stopwatch.GetElapsedTime(_started).TotalSeconds, tags);
        _logScope?.Dispose();
    }
}
