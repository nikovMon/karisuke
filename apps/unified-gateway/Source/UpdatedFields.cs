using System.Text;

namespace ImagingPipeline.UnifiedGateway.Source;

/// <summary>Reads the <c>x-updated-fields</c> header, which lists the overlay fields an update changed.</summary>
internal static class UpdatedFields
{
    public const string HeaderName = "x-updated-fields";

    public static bool Contains(IReadOnlyDictionary<string, object?>? headers, string field)
    {
        if (headers is null || !TryGetHeader(headers, out var value) || value is not IEnumerable<object> fields)
        {
            return false;
        }

        // AMQP delivers header strings as byte arrays.
        return fields.Any(item => item switch
        {
            string text => text == field,
            byte[] bytes => Encoding.UTF8.GetString(bytes) == field,
            _ => false
        });
    }

    private static bool TryGetHeader(IReadOnlyDictionary<string, object?> headers, out object? value)
    {
        if (headers.TryGetValue(HeaderName, out value))
        {
            return true;
        }

        value = headers.FirstOrDefault(header => string.Equals(header.Key, HeaderName, StringComparison.OrdinalIgnoreCase)).Value;
        return value is not null;
    }
}
