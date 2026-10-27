using System.Text.Json;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Options;
using RabbitMQ.Client.Exceptions;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed record AutoRunPublishResult(AutoRunItemOutcome Outcome, string? Error = null);

public interface IAutoRunPublisher
{
    Task<AutoRunPublishResult> PublishAsync(
        string runId, byte[] payload, CancellationToken cancellationToken);
}

public sealed class AutoRunPublisher(
    IRabbitMqDestinationPublisher publisher, IOptions<AutoRunOptions> options,
    ILogger<AutoRunPublisher> logger) : IAutoRunPublisher
{
    public async Task<AutoRunPublishResult> PublishAsync(
        string runId, byte[] payload, CancellationToken cancellationToken)
    {
        if (options.Value.Destination is not { } destination)
            return new(AutoRunItemOutcome.Failed, "Gateway destination is not configured.");
        string messageId;
        try
        {
            using var overlay = JsonDocument.Parse(payload);
            var id = overlay.RootElement.GetProperty("id").GetString()!;
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
            // Use the gateway's ordinary overlay contract and update trigger in every source mode.
            IReadOnlyDictionary<string, object?> headers =
                new Dictionary<string, object?> { ["x-updated-fields"] = new List<object> { "gridType" } };
            await publisher.PublishAsync(destination,
                new RabbitMqMessageEnvelope(messageId, payload, CorrelationId: runId, Headers: headers), cancellationToken);
            return new(AutoRunItemOutcome.Confirmed);
        }
        catch (PublishException ex)
        {
            logger.LogWarning(ex, "RabbitMQ rejected auto-run {AutoRunId} message {MessageId}.", runId, messageId);
            return new(AutoRunItemOutcome.Failed, ex.IsReturn ? "Broker returned an unroutable message." : "Broker negatively acknowledged the message.");
        }
        catch (ArgumentException ex)
        {
            logger.LogWarning(ex, "Invalid publication for auto-run {AutoRunId} message {MessageId}.", runId, messageId);
            return new(AutoRunItemOutcome.Failed, "Invalid gateway destination or message.");
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Connection and topology failures can also reach this catch before sending.
            // Preserve the original broker reason for diagnosis without exposing it in the UI.
            logger.LogWarning(ex, "RabbitMQ publication could not be confirmed for auto-run {AutoRunId} message {MessageId}.", runId, messageId);
            return new(AutoRunItemOutcome.Uncertain,
                ex is OperationCanceledException ? "Publication was interrupted; delivery could not be confirmed." : "RabbitMQ publication could not be confirmed; delivery is uncertain. Check Rules API logs for this run's broker error.");
        }
    }
}
