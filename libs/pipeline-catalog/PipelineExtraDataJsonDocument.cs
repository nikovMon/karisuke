using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.PipelineCatalog;

internal static class PipelineExtraDataJsonDocument
{
    public static MemoryStream Preserve(Stream input)
    {
        // Match the normal JSON configuration provider's encoding, comment, and trailing-comma behavior.
        using var reader = new StreamReader(input);
        using var document = JsonDocument.Parse(reader.ReadToEnd(), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        var output = new MemoryStream();
        try
        {
            using (var writer = new Utf8JsonWriter(output))
            {
                Write(document.RootElement, writer, string.Empty);
            }

            output.Position = 0;
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private static void Write(JsonElement value, Utf8JsonWriter writer, string path)
    {
        if (IsExtraData(path))
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"Configuration '{path}' must be a JSON object.");
            }

            // Serialize the intact object to a scalar before IConfiguration flattens it.
            // Serialization removes accepted comments/trailing commas without coercing JSON values.
            writer.WriteStringValue(JsonSerializer.Serialize(value));
            return;
        }

        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in value.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);
                    Write(property.Value, writer, Append(path, property.Name));
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                var index = 0;
                foreach (var item in value.EnumerateArray())
                {
                    Write(item, writer, Append(path, index.ToString(CultureInfo.InvariantCulture)));
                    index++;
                }

                writer.WriteEndArray();
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    private static bool IsExtraData(string path)
    {
        var segments = path.Split(':');
        return segments.Length == 4 &&
            segments[0].Equals(PipelineCatalogOptions.SectionName, StringComparison.OrdinalIgnoreCase) &&
            segments[1].Equals(nameof(PipelineCatalogOptions.Pipelines), StringComparison.OrdinalIgnoreCase) &&
            segments[3].Equals("ExtraData", StringComparison.OrdinalIgnoreCase);
    }

    private static string Append(string path, string segment) => path.Length == 0 ? segment : $"{path}:{segment}";
}
