using System.Globalization;
using System.Text;
using ImagingPipeline.RabbitMqClient;
using RabbitMQ.Client;

namespace ImagingPipeline.DlqRecovery;

public sealed record RecoveryDecision(bool Replay, string Reason, RabbitMqFailure Failure, int RedriveCount);

public static class RecoveryPolicy
{
    public static RecoveryDecision Decide(RecoveryDelivery delivery, RecoveryOptions options)
    {
        var headers = ReadHeaders(delivery);
        if (!RabbitMqFailureHeaders.TryReadFailure(headers, out var failure))
            return new(false, "unknown-failure", new(RabbitMqFailureKind.Unknown,
                "missing-failure-metadata", "The original failure has no reliable classification."), 0);
        if (!RabbitMqFailureHeaders.TryReadNonNegativeInt(headers, "redrive-count", out var count))
            return new(false, "invalid-redrive-count", failure, 0);
        if (count >= options.MaxRedrives)
            return new(false, "redrive-limit", failure, count);
        var eligible = failure.Kind == RabbitMqFailureKind.Transient
            || options.ReplayDependencyResponseFailures && failure.Kind == RabbitMqFailureKind.DependencyResponse;
        return new(eligible, eligible ? "retryable-failure" : "manual-failure", failure, count);
    }

    public static bool IsFromRun(RecoveryDelivery delivery, string runId) =>
        ReadHeaders(delivery).Any(pair => pair.Key.Equals("redrive-run-id", StringComparison.OrdinalIgnoreCase)
            && Text(pair.Value) == runId);

    public static RecoveryDelivery Prepare(RecoveryDelivery delivery, RecoveryDecision decision,
        RecoveryQueueOptions mapping, string runId, DateTimeOffset now)
    {
        var properties = new BasicProperties(delivery.Properties)
        {
            Persistent = true,
            Headers = new Dictionary<string, object?>(ReadHeaders(delivery), StringComparer.Ordinal)
        };
        // Live expiration must not silently remove a retained original or a later replay.
        if (properties.Expiration is { Length: > 0 } expiration)
            properties.Headers["original-expiration"] = expiration;
        properties.Expiration = null;
        // RabbitMQ validates user_id against the NEW publishing connection, not the original producer.
        if (properties.UserId is { Length: > 0 } userId)
            properties.Headers["original-user-id"] = userId;
        properties.UserId = null;
        if (decision.Replay)
        {
            Set(properties.Headers, mapping.RetryCountHeader, 0);
            Set(properties.Headers, "redrive-count", checked(decision.RedriveCount + 1));
            Set(properties.Headers, "last-redrive-at", now.ToString("O", CultureInfo.InvariantCulture));
            Set(properties.Headers, "redrive-run-id", runId);
        }
        else
        {
            Set(properties.Headers, "manual-retention-reason", decision.Reason);
            Set(properties.Headers, "manual-retained-at", now.ToString("O", CultureInfo.InvariantCulture));
        }
        return delivery with { Properties = properties };
    }

    private static IReadOnlyDictionary<string, object?> ReadHeaders(RecoveryDelivery delivery) =>
        delivery.Properties.Headers is null ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(delivery.Properties.Headers);

    private static string? Text(object? value) => value switch
    {
        string text => text,
        byte[] bytes => Encoding.UTF8.GetString(bytes),
        _ => null
    };

    private static void Set(IDictionary<string, object?> headers, string name, object value)
    {
        foreach (var key in headers.Keys.Where(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)).ToArray())
            headers.Remove(key);
        headers[name] = value;
    }
}
