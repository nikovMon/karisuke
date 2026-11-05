using System.Text.Json;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using ImagingPipeline.UnifiedGateway.Rules;

namespace ImagingPipeline.UnifiedGateway.Source;

/// <summary>
/// Handles one image update from the source exchange: matches it against every enabled
/// pipeline's rules, prepares one unit per matched run, dispatches the units, and tells the
/// consumer whether the message succeeded, should be retried, or should be dead-lettered.
/// </summary>
public sealed class SourceMessageHandler(
    GatewayRuleCache ruleCache,
    RuleMatcher matcher,
    PipelineWorkPreparer preparer,
    PipelineDispatcher dispatcher,
    ILogger<SourceMessageHandler> logger) : IRabbitMqMessageHandler
{
    public async Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        if (!UpdatedFields.Contains(message.Headers, "gridType"))
        {
            SourceMessageTelemetry.Filtered(message, logger, "grid_type_not_updated");
            return RabbitMqMessageProcessingResult.Success();
        }

        using var telemetry = SourceMessageTelemetry.Begin(message, logger);
        GatewayInputMessage image;
        PipelineDispatchContext imageContext;
        try
        {
            image = GatewayInputMessageParser.Parse(message.Body);
            imageContext = CreateImageContext(image, message.Body);
        }
        catch (InvalidInputMessageException ex)
        {
            telemetry.Rejected(ex);
            return RabbitMqMessageProcessingResult.NonRetryableFailure(
                new RabbitMqFailure(RabbitMqFailureKind.InvalidData, ex.ErrorCode, "Source message failed input validation.", Operation: "parse_source"));
        }

        telemetry.Parsed(image);
        var preparationFailures = new List<RabbitMqFailure>();
        var units = PrepareUnits(image, imageContext, message, preparationFailures);
        var outcomes = await dispatcher.DispatchAsync(units, cancellationToken);

        var result = Decide(outcomes, preparationFailures);
        telemetry.Completed(outcomes, preparationFailures.Count, result);
        return result;
    }

    private List<DispatchUnit> PrepareUnits(
        GatewayInputMessage image,
        PipelineDispatchContext imageContext,
        RabbitMqMessageEnvelope message,
        List<RabbitMqFailure> preparationFailures)
    {
        var units = new Dictionary<string, DispatchUnit>(StringComparer.Ordinal);
        var pipelines = ruleCache.Current;
        if (pipelines.Count == 0)
        {
            SourceMessageTelemetry.Filtered(message, logger, "no_enabled_pipelines");
        }

        foreach (var pipelineRules in pipelines)
        {
            var pipelineId = pipelineRules.Pipeline.PipelineId;
            using var pipelineScope = logger.BeginScope(new KeyValuePair<string, object?>[] { new(TelemetryAttributeNames.PipelineId, pipelineId) });
            RuleEvaluation evaluation;
            using (var matchTelemetry = RuleMatchTelemetry.Begin(pipelineId, pipelineRules.Rules.Count))
            {
                evaluation = matcher.Match(image, pipelineRules.Rules);
                matchTelemetry.Matched(evaluation);
            }

            logger.LogRulesEvaluated(evaluation);
            if (evaluation.Matches.Count == 0)
            {
                SourceMessageTelemetry.Filtered(message, logger, "no_matching_rules");
            }

            foreach (var match in evaluation.Matches)
            {
                using var ruleScope = logger.BeginTelemetryScope(new TelemetryLogContext(RuleId: match.Rule.Id));
                foreach (var runParams in match.Rule.RunParams)
                {
                    var dispatchId = DispatchIdentity.Create(image.ImageId, pipelineId, match.Rule.Id, runParams);
                    if (units.ContainsKey(dispatchId))
                    {
                        SourceMessageTelemetry.Filtered(message, logger, "duplicate_run");
                        continue;
                    }

                    var prepared = preparer.Prepare(pipelineId, UnitContext(imageContext, dispatchId, match), runParams);
                    if (prepared.Work is { } work)
                    {
                        units[dispatchId] = new DispatchUnit(dispatchId, work, message.MessageId, message.Headers);
                    }
                    else
                    {
                        preparationFailures.Add(prepared.Status == PipelinePreparationStatus.Disabled
                            ? new(RabbitMqFailureKind.Configuration, "gateway.pipeline.disabled", "Pipeline is disabled.", pipelineId, "prepare")
                            : new(RabbitMqFailureKind.InvalidData, "gateway.contract.invalid",
                                $"Pipeline contract validation failed for fields: {string.Join(", ", prepared.Errors.Take(5).Select(error => error.Field))}.",
                                pipelineId, "prepare"));
                    }
                }
            }
        }

        return units.Values.ToList();
    }

    // The image fields every unit shares. Task and rule IDs are replaced per unit.
    private static PipelineDispatchContext CreateImageContext(GatewayInputMessage image, ReadOnlyMemory<byte> body)
    {
        try
        {
            using var overlay = JsonDocument.Parse(body);
            return OverlayDispatchContextFactory.Create(image.ImageId, image.ImageId, overlay.RootElement);
        }
        catch (ArgumentException ex)
        {
            throw new InvalidInputMessageException($"Input overlay cannot be projected for pipelines: {ex.Message}", "gateway.invalid_overlay");
        }
    }

    // The task ID is the dispatch ID, which already identifies the image, pipeline, rule and run.
    private static PipelineDispatchContext UnitContext(PipelineDispatchContext imageContext, string dispatchId, RuleMatchResult match) =>
        imageContext with
        {
            TaskId = dispatchId,
            RuleId = match.Rule.Id,
            // The ROI is the part of the image the rule covers, not the whole footprint.
            RoiFootprint = GeometryUtilities.WriteGeoJson(match.IntersectionGeometry),
            RuleLocationWkt = match.Rule.Geometry is { } location ? GeometryUtilities.WriteWkt(location) : null
        };

    // A retryable failure retries the whole message; targets dedupe the already delivered units
    // by dispatch ID. A permanent failure dead-letters it, naming the pipelines that failed.
    private static RabbitMqMessageProcessingResult Decide(IReadOnlyList<DispatchOutcome> outcomes, IReadOnlyList<RabbitMqFailure> preparationFailures)
    {
        var retryable = Failures(outcomes, DispatchStatus.Retryable);
        if (retryable.Length > 0)
        {
            return RabbitMqMessageProcessingResult.RetryableFailure(Summarize(retryable, "Dispatch can be retried"));
        }

        var rejected = Failures(outcomes, DispatchStatus.Rejected).Concat(preparationFailures).ToArray();
        return rejected.Length > 0
            ? RabbitMqMessageProcessingResult.NonRetryableFailure(Summarize(rejected, "Dispatch was rejected"))
            : RabbitMqMessageProcessingResult.Success();
    }

    private static RabbitMqFailure[] Failures(IReadOnlyList<DispatchOutcome> outcomes, DispatchStatus status) =>
        outcomes.Where(outcome => outcome.Status == status)
            .Select(outcome => outcome.FailureDetails ?? new(RabbitMqFailureKind.Unknown, "gateway.dispatch.unclassified",
                "Dispatch failed without structured failure information.", outcome.Unit.PipelineId, "dispatch", outcome.StatusCode))
            .ToArray();

    private static RabbitMqFailure Summarize(IReadOnlyList<RabbitMqFailure> failures, string action)
    {
        // Retry actions and automatic replay eligibility are separate: an unknown retryable
        // sibling must never acquire a transient label from a known timeout in another unit.
        var primary = failures.OrderBy(failure => failure.Kind == RabbitMqFailureKind.Transient ? 1 : 0)
            .ThenBy(failure => failure.Dependency, StringComparer.Ordinal).First();
        var pipelines = failures.Select(failure => failure.Dependency).OfType<string>()
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(8);
        var message = $"{action} for pipelines: {string.Join(", ", pipelines)}. {primary.Message}";
        return primary with { Message = message.Length <= 1024 ? message : message[..1024] };
    }
}
