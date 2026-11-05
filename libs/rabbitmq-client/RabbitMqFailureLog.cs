using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.RabbitMqClient;

public sealed class RabbitMqFailureLogOptions
{
    public string RetryCountHeader { get; set; } = "retry-count";

    /// <summary>Property names match recursively; dot paths may use * for array items.</summary>
    public List<string> ExcludedPaths { get; set; } =
    [
        "footprint", "roiFootprint", "geometry", "polygon", "polygons", "coordinates",
        "roiCoordinates", "pixelRoi", "groundRoi", "focusedPxWkt", "ruleLocationWkt", "locationWkt",
        "imageData", "binaryData"
    ];
}

public static class RabbitMqFailureLog
{
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 256 };

    /// <summary>Logs a separate sanitized copy. Logging must never prevent a completed retention transfer.</summary>
    public static void Log(
        ILogger logger,
        RabbitMqMessageEnvelope message,
        RabbitMqFailure failure,
        string service,
        string sourceQueue,
        string retainedQueue,
        RabbitMqFailureLogOptions? options = null)
    {
        try
        {
            if (!logger.IsEnabled(LogLevel.Warning))
            {
                return;
            }
            var json = CreateDiagnosticJson(message, failure, service, sourceQueue, retainedQueue, options);
            var id = Guid.NewGuid().ToString("N");
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
            var count = (json.Length + LosslessJsonLogChunk.MaximumChunkLength - 1) / LosslessJsonLogChunk.MaximumChunkLength;
            for (var index = 0; index < count; index++)
            {
                var offset = index * LosslessJsonLogChunk.MaximumChunkLength;
                var chunk = new LosslessJsonLogChunk(id, index, count,
                    json.Substring(offset, Math.Min(LosslessJsonLogChunk.MaximumChunkLength, json.Length - offset)), hash, json.Length);
                logger.Log(LogLevel.Warning, new EventId(117, "retained_message_diagnostic"), chunk, null,
                    static (state, _) => state.ToString());
            }
        }
        catch (Exception)
        {
            // The queue retains the authoritative original. A broken logger cannot undo that transfer.
            try
            {
                logger.LogWarning(new EventId(118, "retained_message_diagnostic_failed"),
                    "Retained message diagnostic could not be logged; the original remains in its retention queue.");
            }
            catch (Exception)
            {
                // Logging providers may themselves fail. Do not trigger a message replay for that failure.
            }
        }
    }

    public static string CreateDiagnosticJson(
        RabbitMqMessageEnvelope message,
        RabbitMqFailure failure,
        string service,
        string sourceQueue,
        string retainedQueue,
        RabbitMqFailureLogOptions? options = null)
    {
        options ??= new RabbitMqFailureLogOptions();
        var excluded = options.ExcludedPaths ?? [];
        var omitted = new List<string>();
        JsonDocument? payload = null;
        string? parseError = null;
        try
        {
            payload = JsonDocument.Parse(message.Body, JsonOptions);
        }
        catch (JsonException)
        {
            parseError = "invalid-json";
        }

        using (payload)
        using (var stream = new MemoryStream())
        {
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 256 }))
            {
                writer.WriteStartObject();
                writer.WriteString("service", service);
                writer.WriteString("sourceQueue", sourceQueue);
                writer.WriteString("retainedQueue", retainedQueue);
                writer.WriteString("messageId", message.MessageId);
                writer.WriteString("correlationId", message.CorrelationId);
                writer.WriteString("contentType", message.ContentType);
                writer.WriteNumber("originalBytes", message.Body.Length);
                writer.WriteString("originalSha256", Convert.ToHexString(SHA256.HashData(message.Body)));
                writer.WriteString("failureClass", RabbitMqFailureHeaders.ToToken(failure.Kind));
                writer.WriteString("failureCode", failure.Code);
                writer.WriteString("failureMessage", SanitizeDiagnosticText(failure.Message, excluded, omitted));
                writer.WriteString("dependency", failure.Dependency);
                writer.WriteString("operation", failure.Operation);
                if (failure.HttpStatus is { } status)
                {
                    writer.WriteNumber("httpStatus", status);
                }
                foreach (var name in new[] { RabbitMqFailureHeaders.FirstFailureAt, RabbitMqFailureHeaders.LastFailureAt,
                             RabbitMqFailureHeaders.LastRedriveAt, RabbitMqFailureHeaders.RedriveRunId })
                {
                    if (RabbitMqFailureHeaders.TryReadText(message.Headers, name, out var value))
                    {
                        writer.WriteString(name, value);
                    }
                }
                if (RabbitMqFailureHeaders.TryReadNonNegativeInt(message.Headers, RabbitMqFailureHeaders.RedriveCount, out var redrive))
                {
                    writer.WriteNumber("redriveCount", redrive);
                }
                if (RabbitMqFailureHeaders.TryReadNonNegativeInt(message.Headers, options.RetryCountHeader, out var retries))
                {
                    writer.WriteNumber("shortRetryCount", retries);
                }
                if (payload is not null)
                {
                    writer.WritePropertyName("original");
                    WriteFiltered(writer, payload.RootElement, "", excluded, omitted);
                }
                else
                {
                    writer.WriteString("parseFailure", parseError);
                }
                writer.WriteStartArray("omittedPaths");
                foreach (var path in omitted)
                {
                    writer.WriteStringValue(path);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }
    }

    private static void WriteFiltered(Utf8JsonWriter writer, JsonElement value, string path,
        IReadOnlyCollection<string> excluded, List<string> omitted)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            writer.WriteStartObject();
            foreach (var property in value.EnumerateObject())
            {
                var childPath = path.Length == 0 ? property.Name : $"{path}.{property.Name}";
                if (excluded.Any(pattern => string.Equals(pattern, property.Name, StringComparison.OrdinalIgnoreCase) ||
                                            string.Equals(pattern, childPath, StringComparison.OrdinalIgnoreCase)))
                {
                    omitted.Add(childPath);
                    continue;
                }
                writer.WritePropertyName(property.Name);
                WriteFiltered(writer, property.Value, childPath, excluded, omitted);
            }
            writer.WriteEndObject();
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            writer.WriteStartArray();
            foreach (var item in value.EnumerateArray())
            {
                WriteFiltered(writer, item, path.Length == 0 ? "*" : $"{path}.*", excluded, omitted);
            }
            writer.WriteEndArray();
        }
        else
        {
            value.WriteTo(writer);
        }
    }

    private static string SanitizeDiagnosticText(string text, IReadOnlyCollection<string> excluded, List<string> omitted)
    {
        // Response JSON embedded in an exception must not bypass the payload exclusions.
        var start = text.IndexOfAny(['{', '[']);
        if (start >= 0)
        {
            try
            {
                using var document = JsonDocument.Parse(text[start..], JsonOptions);
                using var stream = new MemoryStream();
                using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { MaxDepth = 256 }))
                {
                    WriteFiltered(writer, document.RootElement, "", excluded, omitted);
                }
                return text[..start] + Encoding.UTF8.GetString(stream.ToArray());
            }
            catch (JsonException)
            {
                omitted.Add("failureMessage.embeddedPayload");
                return text[..start] + "[unparseable embedded diagnostic omitted]";
            }
        }
        if (text.Contains("POLYGON", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("LINESTRING", StringComparison.OrdinalIgnoreCase))
        {
            omitted.Add("failureMessage.geometry");
            return "[geometry diagnostic omitted; inspect retained failure metadata]";
        }
        return text;
    }
}
