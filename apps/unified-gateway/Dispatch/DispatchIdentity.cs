using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Builds the deterministic dispatch ID: <c>{imageId}:{pipelineId}:{ruleId}:{runParamsHash}</c>.
/// The same image, pipeline, rule and run parameters always produce the same ID, regardless of
/// property order or whitespace in the run parameters, so redeliveries reuse it.
/// </summary>
public static class DispatchIdentity
{
    public static string Create(string imageId, string pipelineId, string ruleId, JsonElement runParams)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(imageId);
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        return $"{imageId}:{pipelineId}:{ruleId}:{HashRunParams(runParams)}";
    }

    internal static string HashRunParams(JsonElement runParams)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteCanonical(writer, runParams);
        }

        return Convert.ToHexStringLower(SHA256.HashData(buffer.WrittenSpan))[..16];
    }

    // Rewrites JSON into one fixed form so equal run params hash equally: object properties are
    // sorted and whitespace dropped. Array order is kept on purpose, since it can carry meaning.
    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(property => property.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
