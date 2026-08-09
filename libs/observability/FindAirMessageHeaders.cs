namespace ImagingPipeline.Observability;

public static class FindAirMessageHeaders
{
    public const string TraceParent = "traceparent";
    public const string TraceState = "tracestate";
    public const string StartedAtUnixMilliseconds = "findair-started-at-unix-ms";
    public const string PublishedAtUnixMilliseconds = "findair-published-at-unix-ms";
    public const string AlgorithmName = "algorithmName";
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
        var algorithmName = NormalizeAlgorithmNameValue(algorithmNames);
        if (algorithmName is not null)
        {
            headers[AlgorithmName] = algorithmName;
        }

        headers[ContractVersion] = CurrentContractVersion;
        return headers;
    }

    public static string? NormalizeAlgorithmNameValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var values = value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CanonicalAlgorithmName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(AlgorithmOrder)
            .ThenBy(name => name, StringComparer.Ordinal)
            .ToArray();

        return values.Length == 0 ? null : string.Join(',', values);
    }

    private static string CanonicalAlgorithmName(string value)
    {
        if (string.Equals(value, "FindAir", StringComparison.OrdinalIgnoreCase))
        {
            return "FindAir";
        }

        return string.Equals(value, "Rpn", StringComparison.OrdinalIgnoreCase)
            ? "Rpn"
            : value;
    }

    private static int AlgorithmOrder(string value) => value switch
    {
        "FindAir" => 0,
        "Rpn" => 1,
        _ => 2
    };

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
