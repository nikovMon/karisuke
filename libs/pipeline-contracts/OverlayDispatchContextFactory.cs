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
        var fieldErrors = new List<ContractValidationError>();
        var area = ReadString(overlay, "areaOfInterest", fieldErrors);
        return new PipelineDispatchContext(
            taskId, ruleId, imageId, roi, timestamp.ToUniversalTime(),
            ReadString(overlay, "sensorType", fieldErrors) ?? string.Empty,
            ReadString(overlay, "imageUrl", fieldErrors) ?? string.Empty,
            ReadInteger(overlay, "width", fieldErrors), ReadInteger(overlay, "height", fieldErrors),
            ReadNumber(overlay, "bestResolution"),
            ReadString(overlay, "sensorName") ?? string.Empty,
            string.IsNullOrWhiteSpace(area) ? null : area.Trim(),
            ReadString(overlay, "gridType") ?? string.Empty,
            ReadString(overlay, "gridURI", fieldErrors) ?? string.Empty)
        {
            OverlayPhotoTime = legacyTime,
            LegId = ReadString(overlay, "legId", fieldErrors),
            PrevOverlayId = ReadString(overlay, "prevOverlayId", fieldErrors),
            NextOverlayId = ReadString(overlay, "nextOverlayId", fieldErrors),
            InputFieldErrors = fieldErrors.AsReadOnly()
        };
    }

    private static string? ReadString(JsonElement overlay, string field, List<ContractValidationError>? errors = null)
    {
        if (!overlay.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String)
        {
            if (errors is null)
                throw new ArgumentException($"Overlay {field} must be a string or null.", nameof(overlay));
            errors.Add(new($"input.{field}", "Must be a string or null."));
            return null;
        }
        return value.GetString();
    }

    private static int ReadInteger(JsonElement overlay, string field, List<ContractValidationError> errors)
    {
        if (!overlay.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number))
        {
            errors.Add(new($"input.{field}", "Must be a 32-bit integer."));
            return 0;
        }
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
