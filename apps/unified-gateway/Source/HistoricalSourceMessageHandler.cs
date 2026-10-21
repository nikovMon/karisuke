using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Historical;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Historical;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.UnifiedGateway.Configuration;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.UnifiedGateway.Source;

/// <summary>
/// Handles an explicit historical envelope using its frozen rule targets. All targets are
/// validated and prepared before any delivery; invalid envelopes never enter live matching.
/// </summary>
public sealed class HistoricalSourceMessageHandler(
    IPipelineCatalog catalog,
    IPipelineContractRegistry contracts,
    RuleMatcher matcher,
    PipelineWorkPreparer preparer,
    PipelineDispatcher dispatcher,
    IOptions<HistoricalInputOptions> options,
    ILogger<HistoricalSourceMessageHandler> logger)
{
    public async Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        HistoricalOverlayEnvelope envelope;
        try
        {
            if (!options.Value.Enabled)
                throw Invalid("Historical gateway input is disabled.", "gateway.historical_disabled");
            envelope = ReadEnvelope(message.Body);
        }
        catch (InvalidInputMessageException ex)
        {
            using var rejected = SourceMessageTelemetry.Begin(message, logger);
            rejected.Rejected(ex);
            return RabbitMqMessageProcessingResult.NonRetryableFailure(ex.ErrorCode);
        }

        using var runScope = logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new("auto_run.run_id", envelope.RunId),
            new("unified_gateway.input_kind", HistoricalOverlayEnvelope.MessageKind)
        });
        using var telemetry = SourceMessageTelemetry.Begin(message, logger);
        Activity.Current?.SetTag("auto_run.run_id", envelope.RunId);
        Activity.Current?.SetTag("unified_gateway.input_kind", HistoricalOverlayEnvelope.MessageKind);
        List<DispatchUnit> units;
        try
        {
            var overlayBody = JsonSerializer.SerializeToUtf8Bytes(envelope.Overlay);
            var image = GatewayInputMessageParser.Parse(overlayBody);
            telemetry.Parsed(image);
            var context = SourceMessageHandler.CreateImageContext(image, overlayBody);
            units = PrepareUnits(envelope, image, context, message, cancellationToken);
        }
        catch (InvalidInputMessageException ex)
        {
            telemetry.Rejected(ex);
            return RabbitMqMessageProcessingResult.NonRetryableFailure(ex.ErrorCode);
        }
        catch (ArgumentException ex)
        {
            var invalid = Invalid($"Historical target validation failed: {ex.Message}", "gateway.historical_invalid_target");
            telemetry.Rejected(invalid);
            return RabbitMqMessageProcessingResult.NonRetryableFailure(invalid.ErrorCode);
        }

        var outcomes = await dispatcher.DispatchAsync(units, cancellationToken);
        var result = SourceMessageHandler.Decide(outcomes, []);
        telemetry.Completed(outcomes, 0, result);
        return result;
    }

    /// <summary>
    /// Detect the reserved discriminator or envelope shape before the live gridType gate. Even
    /// an unknown kind is sent to strict historical validation instead of ordinary rule matching.
    /// Invalid ordinary JSON keeps the existing live-message behavior.
    /// </summary>
    internal static bool IsEnvelope(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            var hasOverlay = false;
            var hasTargets = false;
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
                    continue;
                if (reader.ValueTextEquals("kind"))
                    return true;
                hasOverlay |= reader.ValueTextEquals("overlay");
                hasTargets |= reader.ValueTextEquals("targets");
                if (hasOverlay && hasTargets)
                    return true;
            }
        }
        catch (JsonException)
        {
            // The ordinary parser reports malformed live input when its updated-fields gate allows it.
        }

        return false;
    }

    private static HistoricalOverlayEnvelope ReadEnvelope(ReadOnlyMemory<byte> body)
    {
        HistoricalOverlayEnvelope envelope;
        try
        {
            using var json = JsonDocument.Parse(body);
            RejectDuplicateProperties(json.RootElement);
            envelope = JsonSerializer.Deserialize(
                body.Span, HistoricalEnvelopeSerializerContext.Default.HistoricalOverlayEnvelope)
                ?? throw Invalid("Historical input must be an object.", "gateway.historical_invalid_envelope");
        }
        catch (JsonException ex)
        {
            throw Invalid($"Historical input is invalid JSON: {ex.Message}", "gateway.historical_invalid_envelope");
        }

        if (envelope.Kind != HistoricalOverlayEnvelope.MessageKind ||
            envelope.Version != HistoricalOverlayEnvelope.CurrentVersion ||
            string.IsNullOrWhiteSpace(envelope.RunId) ||
            envelope.Overlay.ValueKind != JsonValueKind.Object ||
            envelope.Targets is not { Count: > 0 })
            throw Invalid("Historical kind, version, runId, overlay, and targets must be valid.", "gateway.historical_invalid_envelope");

        return envelope;
    }

    private List<DispatchUnit> PrepareUnits(
        HistoricalOverlayEnvelope envelope,
        GatewayInputMessage image,
        PipelineDispatchContext imageContext,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken)
    {
        var seenTargets = new HashSet<(string PipelineId, string RuleId)>();
        var units = new Dictionary<string, DispatchUnit>(StringComparer.Ordinal);
        foreach (var target in envelope.Targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (target is null || string.IsNullOrWhiteSpace(target.PipelineId) ||
                target.Rule is null || string.IsNullOrWhiteSpace(target.Rule.Id))
                throw Invalid("Every historical target needs a pipeline ID and saved rule ID.", "gateway.historical_invalid_target");

            if (!seenTargets.Add((target.PipelineId, target.Rule.Id)))
                throw Invalid("Historical pipeline/rule targets must be unique.", "gateway.historical_duplicate_target");

            if (!catalog.TryGet(target.PipelineId, out var pipeline) || !pipeline.Enabled ||
                !contracts.TryGet(pipeline.ContractId, out var contract))
                throw Invalid("Historical targets must reference enabled, configured pipelines.", "gateway.historical_invalid_pipeline");

            var rule = HistoricalRuleSnapshot.Build(target.Rule, contract);
            using var scope = logger.BeginScope(new KeyValuePair<string, object?>[]
            {
                new("auto_run.run_id", envelope.RunId),
                new(TelemetryAttributeNames.PipelineId, pipeline.PipelineId),
                new(TelemetryAttributeNames.PipelineRuleId, rule.Id)
            });
            var evaluation = matcher.Match(image, [rule]);
            logger.LogRulesEvaluated(evaluation);
            if (evaluation.Matches.Count != 1)
                throw Invalid("The historical overlay does not match its saved target rule.", "gateway.historical_rule_mismatch");

            var match = evaluation.Matches[0];
            foreach (var runParams in rule.RunParams)
            {
                var dispatchId = DispatchIdentity.CreateHistorical(
                    envelope.RunId, image.ImageId, pipeline.PipelineId, rule.Id, runParams);
                if (units.ContainsKey(dispatchId))
                    continue;

                var prepared = preparer.Prepare(
                    pipeline.PipelineId, SourceMessageHandler.UnitContext(imageContext, dispatchId, match), runParams);
                if (prepared.Work is not { } work)
                    throw Invalid("Historical overlay failed pipeline contract validation.", "gateway.historical_invalid_context");

                units.Add(dispatchId, new DispatchUnit(dispatchId, work, message.MessageId, message.Headers));
            }
        }

        return units.Values.ToList();
    }

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw Invalid("Historical input must not contain duplicate JSON properties.", "gateway.historical_invalid_envelope");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static InvalidInputMessageException Invalid(string message, string code) => new(message, code);
}

[JsonSerializable(typeof(HistoricalOverlayEnvelope))]
internal sealed partial class HistoricalEnvelopeSerializerContext : JsonSerializerContext;
