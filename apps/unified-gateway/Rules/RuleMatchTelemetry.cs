using ImagingPipeline.Observability;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.UnifiedGateway.Rules;

/// <summary>
/// Telemetry for matching an image against one pipeline's rules: begin, report the evaluation,
/// dispose to close the <c>unified_gateway.match_rules</c> span, so a trace shows how long each
/// pipeline's matching took and which rules it matched. A match disposed without an evaluation
/// threw, and its span is marked failed.
/// </summary>
internal sealed class RuleMatchTelemetry : IDisposable
{
    private readonly PipelineSpanScope _span;
    private bool _matched;

    private RuleMatchTelemetry(string pipelineId, int ruleCount)
    {
        _span = PipelineSpanScope.StartStage(PipelineStage.UnifiedGateway, "match_rules", default);
        _span.SetTag(TelemetryAttributeNames.PipelineId, pipelineId);
        _span.SetTag(TelemetryAttributeNames.RulesEvaluatedCount, ruleCount);
    }

    public static RuleMatchTelemetry Begin(string pipelineId, int ruleCount) => new(pipelineId, ruleCount);

    public void Matched(RuleEvaluation evaluation)
    {
        _matched = true;
        _span.SetTag(TelemetryAttributeNames.RulesMatchedCount, evaluation.Matches.Count);
        _span.SetTag(TelemetryAttributeNames.RulesMatchedIds, evaluation.Matches.Select(match => match.Rule.Id).ToArray());
    }

    public void Dispose()
    {
        if (!_matched)
        {
            _span.Failed(TelemetryErrorCategory.Handler, recordException: false);
        }

        _span.Dispose();
    }
}
