namespace ImagingPipeline.Observability;

public static class FindAirMessageHeaders
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string StartedAtUnixMilliseconds = "findair-started-at-unix-ms";
    public const string PublishedAtUnixMilliseconds = "findair-published-at-unix-ms";
    public const string AlgorithmNames = "algorithm_names";
    public const string ContractVersion = "findair-contract-version";
    public const int CurrentContractVersion = 1;

    public static Dictionary<string, object?> Forward(
        IReadOnlyDictionary<string, object?>? source,
        string? algorithmNames = null)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        CopyIfPresent(source, headers, TraceParent);
        CopyIfPresent(source, headers, TraceState);
        CopyIfPresent(source, headers, StartedAtUnixMilliseconds);
        if (!string.IsNullOrWhiteSpace(algorithmNames))
        {
            headers[AlgorithmNames] = algorithmNames;
        }

        headers[ContractVersion] = CurrentContractVersion;
        return headers;
    }

    private static void CopyIfPresent(
        IReadOnlyDictionary<string, object?>? source,
        IDictionary<string, object?> destination,
        string name)
    {
        if (source is null)
        {
            return;
        }

        foreach (var header in source)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                destination[name] = header.Value;
                return;
            }
        }
    }
}
