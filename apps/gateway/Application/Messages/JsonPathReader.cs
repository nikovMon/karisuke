using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.Gateway.Application.Messages;

public sealed class JsonPathReader
{
    public bool TryRead(JsonElement root, string path, out JsonElement value)
    {
        value = root;

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        foreach (var segment in path.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                if (!value.TryGetProperty(segment, out value))
                {
                    return false;
                }

                continue;
            }

            if (value.ValueKind == JsonValueKind.Array &&
                int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
            {
                if (index < 0 || index >= value.GetArrayLength())
                {
                    return false;
                }

                value = value.EnumerateArray().ElementAt(index);
                continue;
            }

            return false;
        }

        return true;
    }

    public bool TryReadNonEmptyString(JsonElement root, string path, out string value)
    {
        value = string.Empty;
        if (!TryRead(root, path, out var element) || element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        value = text;
        return true;
    }

    public bool TryReadOptionalString(JsonElement root, string path, out string? value)
    {
        value = null;
        if (!TryRead(root, path, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return true;
        }

        if (element.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var text = element.GetString();
        value = string.IsNullOrWhiteSpace(text) ? null : text;
        return true;
    }

    public bool TryReadPositiveDouble(JsonElement root, string path, out double value)
    {
        value = 0;
        return TryRead(root, path, out var element) &&
            element.ValueKind == JsonValueKind.Number &&
            element.TryGetDouble(out value) &&
            value > 0 &&
            !double.IsNaN(value) &&
            !double.IsInfinity(value);
    }
}
