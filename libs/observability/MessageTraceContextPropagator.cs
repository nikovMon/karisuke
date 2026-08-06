using System.Diagnostics;
using System.Text;
using OpenTelemetry;
using OpenTelemetry.Context.Propagation;

namespace ImagingPipeline.Observability;

public interface IMessageTraceContextPropagator
{
    PropagationContext Extract(IReadOnlyDictionary<string, object?>? headers);

    void InjectCurrent(IDictionary<string, object?> headers);

    void Inject(IDictionary<string, object?> headers, ActivityContext activityContext);
}

public sealed class W3CMessageTraceContextPropagator : IMessageTraceContextPropagator
{
    private static readonly string[] PropagationHeaderNames = ["traceparent", "tracestate", "baggage"];

    private static readonly TextMapPropagator TraceContext = new TraceContextPropagator();

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

        return new PropagationContext(trace.ActivityContext, default);
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
        // allocating; only allocate a cleanup list for unusual external casing.
        foreach (var headerName in PropagationHeaderNames)
        {
            headers.Remove(headerName);
        }

        List<string>? casingVariants = null;
        foreach (var existingKey in headers.Keys)
        {
            if (IsPropagationHeader(existingKey))
            {
                (casingVariants ??= []).Add(existingKey);
            }
        }

        if (casingVariants is not null)
        {
            foreach (var existingKey in casingVariants)
            {
                headers.Remove(existingKey);
            }
        }

        TraceContext.Inject(
            new PropagationContext(activityContext, default),
            headers,
            static (carrier, key, value) => carrier[key] = Encoding.UTF8.GetBytes(value));


    }

    private static bool IsPropagationHeader(string key) =>
        PropagationHeaderNames.Contains(key, StringComparer.OrdinalIgnoreCase);

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
