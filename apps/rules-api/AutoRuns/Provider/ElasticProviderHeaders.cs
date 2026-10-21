namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

/// <summary>Validates operator-supplied request headers without including their values in errors.</summary>
internal static class ElasticProviderHeaders
{
    internal const string ValidationError = "Elastic Provider headers must contain valid request header names and non-null values without invalid controls; content and transport-managed headers are not allowed.";

    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Host", "Connection", "Transfer-Encoding", "TE", "Trailer", "Upgrade", "Keep-Alive",
        "Proxy-Connection", "Proxy-Authenticate", "Proxy-Authorization", "Expect"
    };

    internal static bool IsValid(IReadOnlyDictionary<string, string>? headers)
    {
        if (headers is null) return false;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var request = new HttpRequestMessage();
        foreach (var (name, value) in headers)
        {
            if (string.IsNullOrEmpty(name) || !names.Add(name) ||
                name.Any(character => !char.IsAsciiLetterOrDigit(character) && !"!#$%&'*+-.^_`|~".Contains(character)) ||
                name.StartsWith("Content-", StringComparison.OrdinalIgnoreCase) || ManagedHeaders.Contains(name) ||
                value is null || value.Any(character => char.IsControl(character) && character != '\t') ||
                !request.Headers.TryAddWithoutValidation(name, value))
                return false;
        }
        return true;
    }

    internal static bool HasValidConfigurationShape(IConfigurationSection section) =>
        section.Value is null && section.GetChildren().All(header =>
            header.Value is not null && !header.GetChildren().Any());

    internal static IReadOnlyDictionary<string, string> Snapshot(IReadOnlyDictionary<string, string>? headers)
    {
        if (!IsValid(headers)) throw new ElasticProviderException(ValidationError);
        return new Dictionary<string, string>(headers!, StringComparer.OrdinalIgnoreCase);
    }
}
