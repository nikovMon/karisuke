using System.Collections;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Observability;

public readonly record struct TelemetryLogContext(
    string? MessageId = null,
    string? CorrelationId = null,
    string? Destination = null,
    int? RetryAttempt = null,
    string? TaskId = null,
    string? RequestId = null,
    string? ImageId = null,
    string? RuleId = null,
    string? TenantId = null,
    string? AlgorithmName = null) : IReadOnlyList<KeyValuePair<string, object?>>
{
    public int Count =>
        Present(MessageId) +
        Present(CorrelationId) +
        Present(Destination) +
        (RetryAttempt.HasValue ? 1 : 0) +
        Present(TaskId) +
        Present(RequestId) +
        Present(ImageId) +
        Present(RuleId) +
        Present(TenantId) +
        Present(AlgorithmName);

    public KeyValuePair<string, object?> this[int index]
    {
        get
        {
            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index));
            }

            if (TryTake(ref index, "messaging.message.id", MessageId, out var item)
                || TryTake(ref index, "messaging.message.conversation_id", CorrelationId, out item)
                || TryTake(ref index, "messaging.destination.name", Destination, out item)
                || TryTake(ref index, TelemetryAttributeNames.RetryAttempt, RetryAttempt, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineTaskId, TaskId, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineRequestId, RequestId, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineImageId, ImageId, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineRuleId, RuleId, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineTenantId, TenantId, out item)
                || TryTake(ref index, TelemetryAttributeNames.PipelineAlgorithmName, AlgorithmName, out item))
            {
                return item;
            }

            throw new ArgumentOutOfRangeException(nameof(index));
        }
    }

    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        var count = Count;
        for (var index = 0; index < count; index++)
        {
            yield return this[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private static int Present(string? value) => string.IsNullOrWhiteSpace(value) ? 0 : 1;

    private static bool TryTake(
        ref int index,
        string key,
        string? value,
        out KeyValuePair<string, object?> item)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            item = default;
            return false;
        }

        if (index-- == 0)
        {
            item = new KeyValuePair<string, object?>(key, value);
            return true;
        }

        item = default;
        return false;
    }

    private static bool TryTake(
        ref int index,
        string key,
        int? value,
        out KeyValuePair<string, object?> item)
    {
        if (!value.HasValue)
        {
            item = default;
            return false;
        }

        if (index-- == 0)
        {
            item = new KeyValuePair<string, object?>(key, value.Value);
            return true;
        }

        item = default;
        return false;
    }
}

public static class TelemetryLogScope
{
    public static IDisposable BeginTelemetryScope(
        this ILogger logger,
        in TelemetryLogContext context)
    {
        ArgumentNullException.ThrowIfNull(logger);

        return context.Count == 0
            ? NullScope.Instance
            : logger.BeginScope(context) ?? NullScope.Instance;
    }

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
