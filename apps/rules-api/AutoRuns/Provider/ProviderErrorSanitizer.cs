using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

/// <summary>Formats a bounded error-body excerpt without exposing request credentials or cursors.</summary>
internal static class ProviderErrorSanitizer
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions PatternOptions = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    public static string Sanitize(ReadOnlySpan<byte> body, Uri requestUri, IEnumerable<string> headerValues)
    {
        var secrets = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in headerValues)
        {
            AddSecret(value);
            var separator = value.IndexOf(' ');
            if (separator > 0) AddSecret(value[(separator + 1)..]);
        }
        foreach (var part in requestUri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=');
            if (separator < 0) continue;
            var name = Uri.UnescapeDataString(part[..separator]);
            if (name.Equals("to", StringComparison.OrdinalIgnoreCase) || name.Equals("result_type", StringComparison.OrdinalIgnoreCase))
                continue;
            AddSecret(part[(separator + 1)..]);
            AddSecret(Uri.UnescapeDataString(part[(separator + 1)..]));
        }

        var text = Encoding.UTF8.GetString(body);
        try
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                using var buffer = new MemoryStream();
                using (var writer = new Utf8JsonWriter(buffer)) Write(document.RootElement, writer);
                text = Encoding.UTF8.GetString(buffer.ToArray());
            }
            catch (JsonException) { text = Clean(text); }
            // Redaction can expand short secrets; bound the retained excerpt as well as the bytes read.
            return text.Length <= 8192 ? text : text[..8192];
        }
        catch (RegexMatchTimeoutException) { return "[Provider error body omitted: sanitization limit reached.]"; }

        void AddSecret(string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            secrets.Add(value);
            secrets.Add(Uri.EscapeDataString(value));
            var json = JsonSerializer.Serialize(value);
            secrets.Add(json[1..^1]);
        }

        string Clean(string value)
        {
            value = Regex.Replace(value, @"https?:(?:\\/|/){2}[^\s<>""']+", "[URL redacted]", PatternOptions, RegexTimeout);
            value = Regex.Replace(value, @"\b(?:Bearer|Basic)\s+[A-Za-z0-9+/_=.-]+", "[credential redacted]", PatternOptions, RegexTimeout);
            value = Regex.Replace(value,
                @"(?:authorization|proxy-authorization|cookie|set-cookie|password|secret|credential|api[-_]?key|access[-_]?token|refresh[-_]?token|token|search[-_]?after|cursor|next[-_]?page)[\w-]*[""']?\s*[:=]\s*(?:""[^""]*(?:""|$)|'[^']*(?:'|$)|\[[^\]]*(?:\]|$)|[^\s,;<>]+)",
                "[sensitive value redacted]", PatternOptions, RegexTimeout);
            foreach (var secret in secrets.OrderByDescending(s => s.Length))
                value = value.Replace(secret, "[redacted]", StringComparison.Ordinal);
            return new string(value.Where(c => !char.IsControl(c) || c is '\n' or '\r' or '\t').ToArray());
        }

        void Write(JsonElement element, Utf8JsonWriter writer)
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in element.EnumerateObject())
                    {
                        writer.WritePropertyName(Clean(property.Name));
                        var name = property.Name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal);
                        if (Regex.IsMatch(name, "authorization|cookie|password|secret|credential|apikey|token|searchafter|cursor|nextpage|headers?", PatternOptions, RegexTimeout))
                            writer.WriteStringValue("[redacted]");
                        else Write(property.Value, writer);
                    }
                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in element.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    writer.WriteStringValue(Clean(element.GetString()!));
                    break;
                default:
                    element.WriteTo(writer);
                    break;
            }
        }
    }
}
