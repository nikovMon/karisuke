using System.Text.Json;
using System.Text.Json.Nodes;

namespace ImagingPipeline.RabbitMqClient;

internal enum RabbitMqRetryBuildStatus
{
    Retry,
    AttemptsExhausted,
    InvalidMessage
}

internal sealed record RabbitMqRetryBuildResult(
    RabbitMqRetryBuildStatus Status,
    RabbitMqMessageEnvelope? Message,
    int CurrentRetryCount,
    int NextRetryCount,
    string? Error);

internal static class RabbitMqRetryMessageBuilder
{
    public static RabbitMqRetryBuildResult Build(
        RabbitMqMessageEnvelope message,
        string retryCountPath,
        int maxRetryAttempts)
    {
        var segments = retryCountPath.Split(
            '.',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (segments.Length == 0)
        {
            return Invalid("Retry count path must not be empty.");
        }

        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(message.Body);
        }
        catch (JsonException ex)
        {
            return Invalid($"Message body is not valid JSON: {ex.Message}");
        }

        if (parsed is not JsonObject root)
        {
            return Invalid("Message body must be a JSON object to update retry count.");
        }

        var current = root;
        for (var segmentIndex = 0; segmentIndex < segments.Length - 1; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            if (!current.TryGetPropertyValue(segment, out var child) || child is null)
            {
                var next = new JsonObject();
                current[segment] = next;
                current = next;
                continue;
            }

            if (child is not JsonObject childObject)
            {
                return Invalid($"Retry count path segment '{segment}' is not a JSON object.");
            }

            current = childObject;
        }

        var leaf = segments[^1];
        var currentRetryCount = 0;
        if (current.TryGetPropertyValue(leaf, out var retryCountNode) && retryCountNode is not null)
        {
            if (retryCountNode is not JsonValue retryCountValue ||
                !retryCountValue.TryGetValue<int>(out currentRetryCount) ||
                currentRetryCount < 0)
            {
                return Invalid($"Retry count path '{retryCountPath}' must contain a non-negative integer.");
            }
        }

        if (currentRetryCount >= maxRetryAttempts)
        {
            return new RabbitMqRetryBuildResult(
                RabbitMqRetryBuildStatus.AttemptsExhausted,
                null,
                currentRetryCount,
                currentRetryCount,
                null);
        }

        var nextRetryCount = currentRetryCount + 1;
        current[leaf] = nextRetryCount;
        return new RabbitMqRetryBuildResult(
            RabbitMqRetryBuildStatus.Retry,
            message with { Body = JsonSerializer.SerializeToUtf8Bytes(root) },
            currentRetryCount,
            nextRetryCount,
            null);
    }

    private static RabbitMqRetryBuildResult Invalid(string error) =>
        new(
            RabbitMqRetryBuildStatus.InvalidMessage,
            null,
            0,
            0,
            error);
}
