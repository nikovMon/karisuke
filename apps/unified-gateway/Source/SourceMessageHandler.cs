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
            return RabbitMqMessageProcessingResult.NonRetryableFailure(ex.ErrorCode);
        }

        telemetry.Parsed(image);
        var invalidPipelines = new List<string>();
        var units = PrepareUnits(image, imageContext, message, invalidPipelines);
        var outcomes = await dispatcher.DispatchAsync(units, cancellationToken);

        var result = Decide(outcomes, invalidPipelines);
        telemetry.Completed(outcomes, invalidPipelines.Count, result);
        return result;
    }

    private List<DispatchUnit> PrepareUnits(
        GatewayInputMessage image,
        PipelineDispatchContext imageContext,
        RabbitMqMessageEnvelope message,
        List<string> invalidPipelines)
    {
        var units = new Dictionary<string, DispatchUnit>(StringComparer.Ordinal);
        foreach (var pipelineRules in ruleCache.Current)
        {
            var pipelineId = pipelineRules.Pipeline.PipelineId;
            using var pipelineScope = logger.BeginScope(new KeyValuePair<string, object?>[] { new(TelemetryAttributeNames.PipelineId, pipelineId) });
            var evaluation = matcher.Match(image, pipelineRules.Rules);
            logger.LogRulesEvaluated(evaluation);

            foreach (var match in evaluation.Matches)
            {
                using var ruleScope = logger.BeginTelemetryScope(new TelemetryLogContext(RuleId: match.Rule.Id));
                foreach (var runParams in match.Rule.RunParams)
                {
                    var dispatchId = DispatchIdentity.Create(image.ImageId, pipelineId, match.Rule.Id, runParams);
                    if (units.ContainsKey(dispatchId))
                    {
                        continue;
                    }

                    var prepared = preparer.Prepare(pipelineId, UnitContext(imageContext, dispatchId, match), runParams);
                    if (prepared.Work is { } work)
                    {
                        units[dispatchId] = new DispatchUnit(dispatchId, work, message.MessageId, message.Headers);
                    }
                    else
                    {
                        invalidPipelines.Add(pipelineId);
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
    private static RabbitMqMessageProcessingResult Decide(IReadOnlyList<DispatchOutcome> outcomes, IReadOnlyList<string> invalidPipelines)
    {
        var retryable = FailedPipelines(outcomes, DispatchStatus.Retryable);
        if (retryable.Count > 0)
        {
            return RabbitMqMessageProcessingResult.RetryableFailure($"Dispatch can be retried for pipelines: {string.Join(", ", retryable)}");
        }

        var rejected = FailedPipelines(outcomes, DispatchStatus.Rejected).Union(invalidPipelines).Order(StringComparer.Ordinal).ToList();
        return rejected.Count > 0
            ? RabbitMqMessageProcessingResult.NonRetryableFailure($"Dispatch was rejected for pipelines: {string.Join(", ", rejected)}")
            : RabbitMqMessageProcessingResult.Success();
    }

    private static List<string> FailedPipelines(IReadOnlyList<DispatchOutcome> outcomes, DispatchStatus status) =>
        outcomes.Where(outcome => outcome.Status == status)
            .Select(outcome => outcome.Unit.PipelineId)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToList();
}
