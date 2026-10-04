using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using ImagingPipeline.GeometryUtils;

namespace ImagingPipeline.PipelineContracts;

/// <summary>Builds one Algo Manager mission request for an already selected rule and overlay.</summary>
public sealed class AlgoPipelineContract : IPipelineContract
{
    private static readonly FrozenSet<string> RunParamFields = new[]
    {
        "customer", "profile_name", "hebrew_rule_name", "algorithm_name", "priority", "username",
        "run_every_other_image", "should_check_in_vip", "location_geojson"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenSet<string> SettingFields = new[]
    {
        "XUserName", "Origin", "QueueType", "SaveDetections"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string> NoAttributes =
        new Dictionary<string, string>().ToFrozenDictionary(StringComparer.Ordinal);

    public string ContractId => "algo";

    public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams)
    {
        var errors = new List<ContractValidationError>();
        ParseRunParams(runParams, errors);
        return errors.AsReadOnly();
    }

    public IReadOnlyList<ContractValidationError> ValidateExtraData(JsonElement extraData)
    {
        var errors = new List<ContractValidationError>();
        ParseSettings(extraData, errors);
        return errors.AsReadOnly();
    }

    public IReadOnlyList<ContractValidationError> ValidateContext(PipelineDispatchContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        if (string.IsNullOrWhiteSpace(context.ImageId))
            errors.Add(new("input.id", "Must be a nonempty string."));
        if (PhotoTime(context) == default)
            errors.Add(new("input.photoTime", "Must be supplied."));
        return errors.AsReadOnly();
    }

    public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        var parameters = ParseRunParams(runParams, errors);
        ThrowIfInvalid(errors, nameof(runParams));
        var settings = ParseSettings(extraData, errors);
        ThrowIfInvalid(errors, nameof(extraData));
        errors.AddRange(ValidateContext(context));
        ThrowIfInvalid(errors, nameof(context));
        var photoTime = PhotoTime(context);

        var buffer = new ArrayBufferWriter<byte>();
        // Algo Manager receives JSON directly; preserve the legacy serializer's unescaped Unicode.
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteString("modelName", parameters.ProfileName);
        writer.WriteString("focusedWkt", parameters.FocusedWkt);
        writer.WriteString("origin", settings.Origin);
        writer.WriteString("queueType", settings.QueueType);
        writer.WriteNumber("priority", parameters.Priority);
        writer.WriteBoolean("saveDetections", settings.SaveDetections);
        writer.WriteString("algorithmName", parameters.AlgorithmName);
        writer.WriteString("missionName", $"{parameters.HebrewRuleName} {photoTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
        writer.WriteString("username", parameters.Username ?? settings.XUserName);
        writer.WriteString("displayName", parameters.Customer);
        writer.WriteString("requestingUnit", parameters.Customer);
        writer.WriteBoolean("runEveryOtherImage", parameters.RunEveryOtherImage);
        writer.WriteBoolean("shouldCheckInVip", parameters.ShouldCheckInVip);
        writer.WriteStartArray("tasksData");
        writer.WriteStartObject();
        writer.WriteString("imageId", context.ImageId);
        writer.WriteString("legId", parameters.RunEveryOtherImage ? context.LegId : null);
        writer.WriteString("prevOverlayId", parameters.RunEveryOtherImage ? context.PrevOverlayId : null);
        writer.WriteString("nextOverlayId", parameters.RunEveryOtherImage ? context.NextOverlayId : null);
        if (parameters.ShouldCheckInVip)
            writer.WriteString("photoTime", photoTime);
        else
            writer.WriteNull("photoTime");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return new(buffer.WrittenSpan.ToArray(), "application/json", NoAttributes);
    }

    private static DateTime PhotoTime(PipelineDispatchContext context) => context.OverlayPhotoTime ?? context.PhotoTime.DateTime;

    private static AlgoRunParameters ParseRunParams(JsonElement value, List<ContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("runParams", "Must be an object."));
            return new("", null, null, "", 0, null, false, false, "");
        }
        ValidateProperties(value, RunParamFields, allowUnknown: false, errors);
        return new(
            ReadRequiredString(value, "customer", errors),
            ReadNullableString(value, "profile_name", errors),
            ReadNullableString(value, "hebrew_rule_name", errors),
            ReadRequiredString(value, "algorithm_name", errors),
            ReadInteger(value, "priority", errors),
            ReadNullableString(value, "username", errors),
            ReadBoolean(value, "run_every_other_image", errors),
            ReadBoolean(value, "should_check_in_vip", errors),
            ReadRuleWkt(value, errors));
    }

    private static AlgoSettings ParseSettings(JsonElement value, List<ContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("extraData", "Must be an object containing Algo settings."));
            return new("", "", "", false);
        }
        ValidateProperties(value, SettingFields, allowUnknown: true, errors);
        return new(
            ReadRequiredString(value, "XUserName", errors),
            ReadRequiredString(value, "Origin", errors),
            ReadRequiredString(value, "QueueType", errors),
            ReadBoolean(value, "SaveDetections", errors));
    }

    private static string ReadRuleWkt(JsonElement value, List<ContractValidationError> errors)
    {
        if (value.TryGetProperty("location_geojson", out var geoJson) && geoJson.ValueKind == JsonValueKind.Object)
        {
            try
            {
                // This is the original rule geometry, not an intersection or the input ROI.
                var geometry = GeometryUtilities.ReadGeoJson(geoJson);
                if (geometry.Coordinates.All(coordinate => double.IsFinite(coordinate.X) && double.IsFinite(coordinate.Y)))
                    return GeometryUtilities.WriteWkt(geometry);
            }
            catch (Exception exception) when (exception is GeometryValidationException or Newtonsoft.Json.JsonException
                or ArgumentException or FormatException or InvalidOperationException or NullReferenceException)
            {
                // Surface a field error without logging rule geometry or library exception contents.
            }
        }
        errors.Add(new("location_geojson", "Must be a valid, nonempty GeoJSON geometry with finite coordinates."));
        return string.Empty;
    }

    private static string ReadRequiredString(JsonElement value, string name, List<ContractValidationError> errors)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(property.GetString()))
            return property.GetString()!;
        errors.Add(new(name, "Must be a nonempty string."));
        return string.Empty;
    }

    private static string? ReadNullableString(JsonElement value, string name, List<ContractValidationError> errors)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        if (property.ValueKind == JsonValueKind.String)
            return property.GetString();
        errors.Add(new(name, "Must be a string or null when provided."));
        return null;
    }

    private static bool ReadBoolean(JsonElement value, string name, List<ContractValidationError> errors)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return property.GetBoolean();
        errors.Add(new(name, "Must be a boolean."));
        return false;
    }

    private static int ReadInteger(JsonElement value, string name, List<ContractValidationError> errors)
    {
        if (value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var result))
            return result;
        errors.Add(new(name, "Must be a 32-bit integer."));
        return 0;
    }

    private static void ValidateProperties(JsonElement value, FrozenSet<string> known, bool allowUnknown, List<ContractValidationError> errors)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            if (!known.Contains(property.Name))
            {
                if (!allowUnknown) errors.Add(new(property.Name, "Unknown parameter."));
            }
            else if (!seen.Add(property.Name))
                errors.Add(new(property.Name, "Duplicate parameters are not allowed."));
        }
    }

    private static void ThrowIfInvalid(List<ContractValidationError> errors, string parameterName)
    {
        if (errors.Count != 0)
            throw new ArgumentException(string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")), parameterName);
    }

    private sealed record AlgoRunParameters(string Customer, string? ProfileName, string? HebrewRuleName,
        string AlgorithmName, int Priority, string? Username, bool RunEveryOtherImage, bool ShouldCheckInVip, string FocusedWkt);

    private sealed record AlgoSettings(string XUserName, string Origin, string QueueType, bool SaveDetections);
}
