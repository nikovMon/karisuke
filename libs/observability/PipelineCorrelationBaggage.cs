using System.Text;
using OpenTelemetry;

namespace ImagingPipeline.Observability;

/// <summary>
/// High-cardinality business correlation carried only in trace baggage, never in metric tags.
/// </summary>
public readonly record struct PipelineCorrelationContext(
    string? TaskId = null,
    string? RequestId = null,
    string? ImageId = null,
    string? RuleId = null,
    string? TenantId = null,
    string? AlgorithmName = null);

/// <summary>
/// Creates a bounded ambient baggage scope for a logical outbound message.
/// </summary>
public static class PipelineCorrelationBaggage
{
    private const int MaxValueBytes = 256;

    private static readonly string[] CanonicalKeys =
    [
        TelemetryAttributeNames.PipelineTaskId,
        TelemetryAttributeNames.PipelineRequestId,
        TelemetryAttributeNames.PipelineImageId,
        TelemetryAttributeNames.PipelineRuleId,
        TelemetryAttributeNames.PipelineTenantId,
        TelemetryAttributeNames.PipelineAlgorithmName
    ];

    /// <summary>
    /// Replaces the ambient baggage with the supplied authoritative values for the lifetime
    /// of the scope. When <paramref name="includeExistingCanonicalValues"/> is true, only the
    /// six canonical correlation keys are retained before supplied values overwrite them.
    /// Unknown baggage is never copied into the outbound scope.
    /// </summary>
    public static IDisposable Push(
        PipelineCorrelationContext correlation,
        bool includeExistingCanonicalValues = false)
    {
        var previous = Baggage.Current;
        var values = new Dictionary<string, string>(CanonicalKeys.Length, StringComparer.Ordinal);

        if (includeExistingCanonicalValues)
        {
            foreach (var key in CanonicalKeys)
            {
                AddIfValid(values, key, previous.GetBaggage(key));
            }
        }

        SetAuthoritative(values, TelemetryAttributeNames.PipelineTaskId, correlation.TaskId);
        SetAuthoritative(values, TelemetryAttributeNames.PipelineRequestId, correlation.RequestId);
        SetAuthoritative(values, TelemetryAttributeNames.PipelineImageId, correlation.ImageId);
        SetAuthoritative(values, TelemetryAttributeNames.PipelineRuleId, correlation.RuleId);
        SetAuthoritative(values, TelemetryAttributeNames.PipelineTenantId, correlation.TenantId);
        SetAuthoritative(values, TelemetryAttributeNames.PipelineAlgorithmName, correlation.AlgorithmName);

        Baggage.Current = values.Count == 0 ? default : Baggage.Create(values);
        return new Scope(previous);
    }

    private static void SetAuthoritative(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (value is null)
        {
            return;
        }

        values.Remove(key);
        AddIfValid(values, key, value);
    }

    private static void AddIfValid(
        IDictionary<string, string> values,
        string key,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && Encoding.UTF8.GetByteCount(value) <= MaxValueBytes)
        {
            values[key] = value;
        }
    }

    private sealed class Scope(Baggage previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Baggage.Current = previous;
            _disposed = true;
        }
    }
}
