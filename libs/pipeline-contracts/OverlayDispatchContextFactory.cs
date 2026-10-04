using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.PipelineContracts;

/// <summary>
/// Projects a flat update-overlay body into contract input. It does not match rules or consume messages.
/// Contracts validate their own required fields; an Algo overlay need not contain ASD image metadata.
/// </summary>
public static class OverlayDispatchContextFactory
{
    public static PipelineDispatchContext Create(string taskId, string ruleId, JsonElement overlay)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(taskId);
        ArgumentException.ThrowIfNullOrWhiteSpace(ruleId);
        if (overlay.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Overlay must be a JSON object.", nameof(overlay));

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in overlay.EnumerateObject())
        {
            if (!names.Add(property.Name))
                throw new ArgumentException("Overlay must not contain duplicate fields.", nameof(overlay));
        }

        var imageId = ReadString(overlay, "id");
        if (string.IsNullOrWhiteSpace(imageId))
            throw new ArgumentException("Overlay id must be a nonempty string.", nameof(overlay));
        var photoTimeText = ReadString(overlay, "photoTime");
        if (!DateTime.TryParse(photoTimeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var legacyTime) ||
            !DateTimeOffset.TryParse(photoTimeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            throw new ArgumentException("Overlay photoTime must be a valid timestamp.", nameof(overlay));

        // Preserve the pictured Algo DateTime.Parse behavior separately from ASD's UTC timestamp.
        // Never substitute the full-image footprint for the explicit ROI.
        var roi = overlay.TryGetProperty("roiFootprint", out var geometry) ? geometry.Clone() : default;
        var area = ReadString(overlay, "areaOfInterest");
        return new PipelineDispatchContext(
            taskId, ruleId, imageId, roi, timestamp.ToUniversalTime(),
            ReadString(overlay, "sensorType") ?? string.Empty,
            ReadString(overlay, "imageUrl") ?? string.Empty,
            ReadInteger(overlay, "width"), ReadInteger(overlay, "height"),
            ReadNumber(overlay, "bestResolution"),
            ReadString(overlay, "sensorName") ?? string.Empty,
            string.IsNullOrWhiteSpace(area) ? null : area.Trim(),
            ReadString(overlay, "gridType") ?? string.Empty,
            ReadString(overlay, "gridURI") ?? string.Empty)
        {
            OverlayPhotoTime = legacyTime,
            LegId = ReadString(overlay, "legId"),
            PrevOverlayId = ReadString(overlay, "prevOverlayId"),
            NextOverlayId = ReadString(overlay, "nextOverlayId")
        };
    }

    private static string? ReadString(JsonElement overlay, string field)
    {
        if (!overlay.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
            throw new ArgumentException($"Overlay {field} must be a string or null.", nameof(overlay));
        return value.GetString();
    }

    private static int ReadInteger(JsonElement overlay, string field)
    {
        if (!overlay.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
            throw new ArgumentException($"Overlay {field} must be a 32-bit integer.", nameof(overlay));
        return number;
    }

    private static double ReadNumber(JsonElement overlay, string field)
    {
        if (!overlay.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number))
            throw new ArgumentException($"Overlay {field} must be a finite number.", nameof(overlay));
        return number;
    }
}
