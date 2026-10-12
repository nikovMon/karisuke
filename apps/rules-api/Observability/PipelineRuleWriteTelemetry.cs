using ImagingPipeline.Observability;

namespace ImagingPipeline.Rules.Api.Observability;

/// <summary>
/// Logs for one write to a pipeline's rules: begin, report the outcome, dispose. Owns the log
/// scope and the outcome logs, so the rule service stays free of logging. Operation metrics and
/// spans come from <see cref="RulesOperationFilter"/>.
/// </summary>
internal sealed class PipelineRuleWriteTelemetry : IDisposable
{
    private readonly ILogger _logger;
    private readonly IDisposable? _logScope;

    private PipelineRuleWriteTelemetry(string pipelineId, string? ruleId, ILogger logger)
    {
        _logger = logger;
        _logScope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new(TelemetryAttributeNames.PipelineId, pipelineId),
            new(TelemetryAttributeNames.PipelineRuleId, ruleId)
        });
    }

    public static PipelineRuleWriteTelemetry Begin(string pipelineId, string? ruleId, ILogger logger) =>
        new(pipelineId, ruleId, logger);

    public void Created(string ruleId)
    {
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.PipelineRuleId, ruleId)
               }))
        {
            _logger.PipelineRuleCreated();
        }
    }

    public void Updated(IReadOnlyCollection<string> changedFields)
    {
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleChangedFields, changedFields.Order(StringComparer.Ordinal).ToArray())
               }))
        {
            _logger.PipelineRuleUpdated();
        }
    }

    public void Deleted() => _logger.PipelineRuleDeleted();

    public void ValidationFailed(IReadOnlyList<string> errors)
    {
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleValidationErrors, errors.ToArray())
               }))
        {
            _logger.PipelineRuleValidationFailed();
        }
    }

    public void NotFound() => _logger.PipelineRuleNotFound();

    public void NameConflict() => _logger.PipelineRuleNameConflict();

    public void Dispose() => _logScope?.Dispose();
}
