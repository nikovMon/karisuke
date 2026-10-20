using System.Buffers;
using System.Collections.Frozen;
using System.Text.Encodings.Web;
using System.Text.Json;
using static ImagingPipeline.PipelineContracts.ContractValidation;

namespace ImagingPipeline.PipelineContracts;

/// <summary>
/// Builds one SAR ATC request: the image ID, the rule's model and frame metadata, and the dispatch
/// ID as <c>requestId</c>, so a retried dispatch carries the same ID. A rule location is optional.
/// </summary>
public sealed class SarAtcPipelineContract : IPipelineContract
{
    private static readonly FrozenSet<string> RunParamFields =
        new[] { "modelName", "frameMetadata" }.ToFrozenSet(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, string> NoAttributes =
        new Dictionary<string, string>().ToFrozenDictionary(StringComparer.Ordinal);
    private static readonly FrozenDictionary<string, object?> RabbitMqAttributes =
        new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["sar-atc-contract-version"] = 1
        }.ToFrozenDictionary(StringComparer.Ordinal);

    public string ContractId => "SarAtc";

    public IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams)
    {
        var errors = new List<ContractValidationError>();
        ParseRunParams(runParams, errors);
        return errors.AsReadOnly();
    }

    public IReadOnlyList<ContractValidationError> ValidateContext(PipelineDispatchContext context, JsonElement runParams = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        if (string.IsNullOrWhiteSpace(context.TaskId))
            errors.Add(new("taskId", "Must be a nonempty string."));
        if (string.IsNullOrWhiteSpace(context.ImageId))
            errors.Add(new("input.id", "Must be a nonempty string."));
        return errors.AsReadOnly();
    }

    public PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var errors = new List<ContractValidationError>();
        var parameters = ParseRunParams(runParams, errors);
        ThrowIfInvalid(errors, nameof(runParams));
        errors.AddRange(((IPipelineContract)this).ValidateExtraData(extraData));
        ThrowIfInvalid(errors, nameof(extraData));
        errors.AddRange(ValidateContext(context, runParams));
        ThrowIfInvalid(errors, nameof(context));

        var buffer = new ArrayBufferWriter<byte>();
        // Keep frameMetadata's text as stored instead of escaping non-ASCII characters.
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        writer.WriteStartObject();
        writer.WriteString("imageId", context.ImageId);
        writer.WriteString("modelName", parameters.ModelName);
        writer.WritePropertyName("frameMetadata");
        parameters.FrameMetadata.WriteTo(writer);
        writer.WriteString("requestId", context.TaskId);
        if (extraData.ValueKind == JsonValueKind.Object && extraData.EnumerateObject().MoveNext())
        {
            writer.WritePropertyName("extraData");
            extraData.WriteTo(writer);
        }
        writer.WriteEndObject();
        writer.Flush();
        return new(buffer.WrittenSpan.ToArray(), "application/json", NoAttributes, RabbitMqAttributes);
    }

    private static SarAtcRunParameters ParseRunParams(JsonElement value, List<ContractValidationError> errors)
    {
        if (value.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new("runParams", "Must be an object."));
            return new(string.Empty, default);
        }

        ValidateProperties(value, RunParamFields, errors);
        var modelName = ReadRequiredString(value, "modelName", errors);

        if (!value.TryGetProperty("frameMetadata", out var frameMetadata) || frameMetadata.ValueKind != JsonValueKind.Object)
            errors.Add(new("frameMetadata", "Must be a JSON object."));
        return new(modelName, frameMetadata);
    }

    private sealed record SarAtcRunParameters(string ModelName, JsonElement FrameMetadata);
}
