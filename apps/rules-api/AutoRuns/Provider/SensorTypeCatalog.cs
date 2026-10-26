namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

/// <summary>Configured public sensor types and their provider query values; matching retains the public types.</summary>
public static class SensorTypeCatalog
{
    public const int MaximumEntries = 128;
    public const int MaximumValueLength = 128;
    public const string ValidationError =
        "AutoRun.Provider.SensorTypeMappings must contain 1 to 128 entries with nonblank keys and values of at most 128 characters and no surrounding whitespace.";

    public static bool IsValid(IReadOnlyDictionary<string, string>? mappings) =>
        mappings is { Count: > 0 and <= MaximumEntries } &&
        mappings.All(entry => IsValidValue(entry.Key) && IsValidValue(entry.Value));

    public static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string>? mappings)
    {
        if (!IsValid(mappings)) throw new ArgumentException(ValidationError, nameof(mappings));
        return new Dictionary<string, string>(mappings!, StringComparer.Ordinal);
    }

    private static bool IsValidValue(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= MaximumValueLength &&
        string.Equals(value, value.Trim(), StringComparison.Ordinal);
}
