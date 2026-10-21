using System.Text.Json;
using ImagingPipeline.Common.Dtos.Historical;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Exceptions;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed record AutoRunPublishResult(AutoRunItemOutcome Outcome, string? Error = null);

public interface IAutoRunPublisher
{
    Task<AutoRunPublishResult> PublishAsync(
        string runId, byte[] payload, IReadOnlyList<AutoRunResolvedTarget> targets, CancellationToken cancellationToken);
}

public sealed class AutoRunPublisher(
    IRabbitMqDestinationPublisher publisher, IOptions<AutoRunOptions> options) : IAutoRunPublisher
{
    public async Task<AutoRunPublishResult> PublishAsync(
        string runId, byte[] payload, IReadOnlyList<AutoRunResolvedTarget> targets, CancellationToken cancellationToken)
    {
        if (options.Value.Destination is not { } destination)
            return new(AutoRunItemOutcome.Failed, "Gateway destination is not configured.");
        byte[] body;
        string messageId;
        try
        {
            using var overlay = JsonDocument.Parse(payload);
            var id = overlay.RootElement.GetProperty("id").GetString()!;
            body = targets.Count == 0 ? payload : JsonSerializer.SerializeToUtf8Bytes(new HistoricalOverlayEnvelope
            {
                RunId = runId,
                Overlay = overlay.RootElement,
                Targets = targets.Select(t => new HistoricalRuleTarget { PipelineId = t.Target.PipelineId, Rule = t.Rule }).ToList()
            }, AutoRunJson.Options);
            messageId = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
                JsonSerializer.SerializeToUtf8Bytes(new[] { runId, id })));
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            return new(AutoRunItemOutcome.Failed, "Cannot serialize the frozen overlay.");
        }
        // After entering the broker client, an exception without an explicit rejection
        // cannot prove non-delivery. Never automatically republish that ambiguous result.
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyDictionary<string, object?>? headers = targets.Count == 0
                ? new Dictionary<string, object?> { ["x-updated-fields"] = new List<object> { "gridType" } }
                : null;
            await publisher.PublishAsync(destination,
                new RabbitMqMessageEnvelope(messageId, body, CorrelationId: runId, Headers: headers), cancellationToken);
            return new(AutoRunItemOutcome.Confirmed);
        }
        catch (PublishException ex)
        {
            return new(AutoRunItemOutcome.Failed, ex.IsReturn ? "Broker returned an unroutable message." : "Broker negatively acknowledged the message.");
        }
        catch (ArgumentException)
        {
            return new(AutoRunItemOutcome.Failed, "Invalid gateway destination or message.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new(AutoRunItemOutcome.Uncertain,
                ex is OperationCanceledException ? "Publication was interrupted; delivery could not be confirmed." : "Broker confirmation was lost or unavailable; delivery is uncertain.");
        }
    }
}
