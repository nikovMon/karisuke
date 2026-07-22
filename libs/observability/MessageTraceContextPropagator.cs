using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace ImagingPipeline.Observability;

public interface IMessageTraceContextPropagator
{
    PropagationContext Extract(IReadOnlyDictionary<string, object?>? headers);

    Baggage ExtractBaggage(IReadOnlyDictionary<string, object?>? headers);

    void InjectCurrent(IDictionary<string, object?> headers);

    void Inject(IDictionary<string, object?> headers, ActivityContext activityContext);
}

public sealed class W3CMessageTraceContextPropagator : IMessageTraceContextPropagator
{
    private static readonly string[] PropagationHeaderNames = ["traceparent", "tracestate", "baggage"];

    private static readonly HashSet<string> AllowedBaggageKeys = new(StringComparer.Ordinal)
    {
        "tenant",
        TelemetryAttributeNames.PipelineTaskId,
        TelemetryAttributeNames.PipelineRequestId,
        TelemetryAttributeNames.PipelineImageId,
        TelemetryAttributeNames.PipelineRuleId,
        TelemetryAttributeNames.PipelineTenantId,
        TelemetryAttributeNames.PipelineAlgorithmName
    };

    private const int MaxBaggageEntries = 8;
    private const int MaxBaggageKeyBytes = 128;
    private const int MaxBaggageValueBytes = 256;
    private const int MaxBaggageBytes = 2_048;

    private static readonly TextMapPropagator TraceContext = new TraceContextPropagator();
    private static readonly TextMapPropagator BaggageContext = new BaggagePropagator();

    public PropagationContext Extract(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null || headers.Count == 0)
        {
            return default;
        }

        PropagationContext trace;
        try
        {
            trace = TraceContext.Extract(default, headers, ExtractValues);
        }
        catch (Exception)
        {
            // Invalid external transport metadata must never fail business-message processing.
            return default;
        }

        return new PropagationContext(trace.ActivityContext, ExtractBaggage(headers));
    }

    public Baggage ExtractBaggage(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null || headers.Count == 0 || !IsBaggageHeaderWithinLimit(headers))
        {
            return default;
        }

        try
        {
            var extracted = BaggageContext.Extract(default, headers, ExtractValues);
            return FilterBaggage(extracted.Baggage);
        }
        catch (Exception)
        {
            // Invalid external baggage is dropped without affecting trace propagation.
            return default;
        }
    }

    public void InjectCurrent(IDictionary<string, object?> headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var context = Activity.Current?.Context ?? default;
        Inject(headers, context);
    }

    public void Inject(IDictionary<string, object?> headers, ActivityContext activityContext)
    {
        ArgumentNullException.ThrowIfNull(headers);

        // Internally produced headers use canonical lowercase keys. Remove those without
        // allocating; only allocate a cleanup array for unusual external casing.
        foreach (var headerName in PropagationHeaderNames)
        {
            headers.Remove(headerName);
        }

        if (headers.Keys.Any(IsPropagationHeader))
        {
            foreach (var existingKey in headers.Keys
                         .Where(IsPropagationHeader)
                         .ToArray())
            {
                headers.Remove(existingKey);
            }
        }

        TraceContext.Inject(
            new PropagationContext(activityContext, default),
            headers,
            static (carrier, key, value) => carrier[key] = Encoding.UTF8.GetBytes(value));

        var baggage = FilterBaggage(Baggage.Current);
        BaggageContext.Inject(
            new PropagationContext(activityContext, baggage),
            headers,
            static (carrier, key, value) => carrier[key] = Encoding.UTF8.GetBytes(value));
    }

    private static bool IsPropagationHeader(string key) =>
        PropagationHeaderNames.Contains(key, StringComparer.OrdinalIgnoreCase);

    private static bool IsBaggageHeaderWithinLimit(IReadOnlyDictionary<string, object?> headers)
    {
        var raw = FindValue(headers, "baggage");
        return raw switch
        {
            null => true,
            string value => Encoding.UTF8.GetByteCount(value) <= MaxBaggageBytes,
            byte[] value => value.Length <= MaxBaggageBytes,
            ReadOnlyMemory<byte> value => value.Length <= MaxBaggageBytes,
            Memory<byte> value => value.Length <= MaxBaggageBytes,
            _ => false
        };
    }

    private static Baggage FilterBaggage(Baggage baggage)
    {
        Dictionary<string, string>? filtered = null;
        var totalBytes = 0;
        foreach (var item in baggage.GetBaggage())
        {
            if (filtered?.Count >= MaxBaggageEntries || !AllowedBaggageKeys.Contains(item.Key))
            {
                continue;
            }

            var keyBytes = Encoding.UTF8.GetByteCount(item.Key);
            var valueBytes = Encoding.UTF8.GetByteCount(item.Value);
            if (keyBytes > MaxBaggageKeyBytes
                || valueBytes > MaxBaggageValueBytes
                || totalBytes + keyBytes + valueBytes > MaxBaggageBytes)
            {
                continue;
            }

            filtered ??= new Dictionary<string, string>(StringComparer.Ordinal);
            filtered[item.Key] = item.Value;
            totalBytes += keyBytes + valueBytes;
        }

        return filtered is null ? default : Baggage.Create(filtered);
    }

    private static IEnumerable<string> ExtractValues(
        IReadOnlyDictionary<string, object?> headers,
        string key)
    {
        var value = FindValue(headers, key);

        return value switch
        {
            string text => [text],
            byte[] bytes => [Encoding.UTF8.GetString(bytes)],
            ReadOnlyMemory<byte> memory => [Encoding.UTF8.GetString(memory.Span)],
            Memory<byte> memory => [Encoding.UTF8.GetString(memory.Span)],
            _ => []
        };
    }

    private static object? FindValue(
        IReadOnlyDictionary<string, object?> headers,
        string key)
    {
        if (headers.TryGetValue(key, out var value))
        {
            return value;
        }

        foreach (var pair in headers)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return pair.Value;
            }
        }

        return null;
    }
}
