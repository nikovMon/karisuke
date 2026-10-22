using System.Buffers;
using System.Collections.Frozen;
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using static ImagingPipeline.PipelineContracts.ContractValidation;

namespace ImagingPipeline.PipelineContracts;

/// <summary>Builds one DeepWisdom mission request for an already selected rule and overlay.</summary>
public sealed class DeepWisdomPipelineContract : IPipelineContract
{
    private static readonly FrozenSet<string> RunParamFields = new[]
    {
        "customer", "modelName", "hebrewRuleName", "algorithmName", "priority", "username",
        "runEveryOtherImage", "shouldCheckInVip"
    }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string> NoAttributes =
        new Dictionary<string, string>().ToFrozenDictionary(StringComparer.Ordinal);

    public string ContractId => "DeepWisdom";

    public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams)
    {
        var errors = new List<ContractValidationError>();
        ParseRunParams(runParams, errors);
        return errors.AsReadOnly();
    }

    public IReadOnlyList<ContractValidationError> ValidateExtraData(JsonElement extraData) =>
        extraData.ValueKind is JsonValueKind.Object or JsonValueKind.Undefined
            ? []
            : [new("extraData", "Must be a JSON object when provided.")];

    // DeepWisdom receives the rule's area as focusedWkt, so its rules need a location.
    public IReadOnlyList<ContractValidationError> ValidateRuleLocation(string? locationWkt) =>
        string.IsNullOrWhiteSpace(locationWkt)
            ? [new("match.locationWkt", "Must be supplied; DeepWisdom sends the rule's location as focusedWkt.")]
            : [];

    public IReadOnlyList<ContractValidationError> ValidateContext(PipelineDispatchContext context, JsonElement runParams = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        if (string.IsNullOrWhiteSpace(context.ImageId))
            errors.Add(new("input.id", "Must be a nonempty string."));
        if (PhotoTime(context) == default)
            errors.Add(new("input.photoTime", "Must be supplied."));
        errors.AddRange(ValidateRuleLocation(context.RuleLocationWkt));
        if (runParams.ValueKind == JsonValueKind.Object &&
            runParams.TryGetProperty("runEveryOtherImage", out var everyOther) && everyOther.ValueKind == JsonValueKind.True)
        {
            errors.AddRange(context.InputFieldErrors.Where(error => error.Field is
                "input.legId" or "input.prevOverlayId" or "input.nextOverlayId"));
        }
        return errors.AsReadOnly();
    }

    public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        var parameters = ParseRunParams(runParams, errors);
        ThrowIfInvalid(errors, nameof(runParams));
        errors.AddRange(ValidateExtraData(extraData));
        ThrowIfInvalid(errors, nameof(extraData));
        errors.AddRange(ValidateContext(context, runParams));
        ThrowIfInvalid(errors, nameof(context));
        var photoTime = PhotoTime(context);

        var buffer = new ArrayBufferWriter<byte>();
        // DeepWisdom receives JSON directly; preserve the legacy serializer's unescaped Unicode.
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteString("modelName", parameters.ModelName);
        writer.WriteString("focusedWkt", context.RuleLocationWkt);
        WriteConfiguredValue(writer, extraData, "Origin", "origin");
        WriteConfiguredValue(writer, extraData, "QueueType", "queueType");
        writer.WriteNumber("priority", parameters.Priority);
        WriteConfiguredValue(writer, extraData, "SaveDetections", "saveDetections");
        writer.WriteString("algorithmName", parameters.AlgorithmName);
        writer.WriteString("missionName", $"{parameters.HebrewRuleName} {photoTime.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}");
        if (parameters.Username is not null)
            writer.WriteString("username", parameters.Username);
        else if (!WriteConfiguredValue(writer, extraData, "XUserName", "username"))
            writer.WriteNull("username");
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
        if (extraData.ValueKind == JsonValueKind.Object && extraData.EnumerateObject().MoveNext())
        {
            writer.WritePropertyName("extraData");
            extraData.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return new(buffer.WrittenSpan.ToArray(), "application/json", NoAttributes);
    }

    private static bool WriteConfiguredValue(Utf8JsonWriter writer, JsonElement extraData, string setting, string output)
    {
        if (extraData.ValueKind != JsonValueKind.Object || !extraData.TryGetProperty(setting, out var value))
            return false;
        writer.WritePropertyName(output);
        value.WriteTo(writer);
        return true;
    }

    private static DateTime PhotoTime(PipelineDispatchContext context) => context.OverlayPhotoTime ?? context.PhotoTime.DateTime;

    private static DeepWisdomRunParameters ParseRunParams(JsonElement value, List<ContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("runParams", "Must be an object."));
            return new("", null, null, "", 0, null, false, false);
        }
        ValidateProperties(value, RunParamFields, errors);
        return new(
            ReadRequiredString(value, "customer", errors),
            ReadNullableString(value, "modelName", errors),
            ReadNullableString(value, "hebrewRuleName", errors),
            ReadRequiredString(value, "algorithmName", errors),
            ReadInteger(value, "priority", errors),
            ReadNullableString(value, "username", errors),
            ReadBoolean(value, "runEveryOtherImage", errors),
            ReadBoolean(value, "shouldCheckInVip", errors));
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

    private sealed record DeepWisdomRunParameters(string Customer, string? ModelName, string? HebrewRuleName,
        string AlgorithmName, int Priority, string? Username, bool RunEveryOtherImage, bool ShouldCheckInVip);
}
