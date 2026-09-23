namespace ImagingPipeline.PipelineCatalog;

public static class HttpTransportValidation
{
    private static readonly HashSet<string> ManagedHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Length", "Host", "Transfer-Encoding", "Connection", "Content-Type",
        "Trailer", "TE", "Upgrade", "Keep-Alive", "Proxy-Connection"
    };

    public static void ValidateHeaders(
        IReadOnlyDictionary<string, string>? headers,
        string path,
        ICollection<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        if (headers is null)
        {
            errors.Add($"{path} must not be null.");
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var header in headers)
        {
            var field = $"{path}:entry[{index++}]";
            if (!IsToken(header.Key))
            {
                errors.Add($"{field} has an invalid HTTP header name.");
            }
            else if (!names.Add(header.Key))
            {
                errors.Add($"{field} duplicates another header name ignoring case.");
            }
            else if (ManagedHeaders.Contains(header.Key))
            {
                errors.Add($"{field} is managed by the transport or payload contract and cannot be configured.");
            }

            if (header.Value is null || header.Value.Any(character =>
                    character is '\r' or '\n' || (char.IsControl(character) && character != '\t')))
            {
                errors.Add($"{field} has an invalid HTTP header value.");
            }
        }
    }

    private static bool IsToken(string? value) =>
        !string.IsNullOrEmpty(value) && value.All(character =>
            char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character));
}
