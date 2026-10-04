using System.Text.Json;

namespace ImagingPipeline.PipelineContracts;

public interface IPipelineContract
{
    string ContractId { get; }

    IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams);

    /// <summary>Validates per-pipeline contract settings. Each contract owns their meaning.</summary>
    IReadOnlyList<ContractValidationError> ValidateExtraData(JsonElement extraData) =>
        extraData.ValueKind is JsonValueKind.Undefined or JsonValueKind.Object
            ? []
            : [new("extraData", "Must be a JSON object when provided.")];

    /// <summary>Validates only the event fields used by this contract.</summary>
    IReadOnlyList<ContractValidationError> ValidateContext(PipelineDispatchContext context) => [];

    /// <summary>
    /// Builds the downstream payload from event data, selected rule parameters and per-pipeline
    /// settings. Each contract defines how ExtraData contributes to its payload.
    /// </summary>
    PipelinePayload BuildPayload(PipelineDispatchContext context, JsonElement runParams, JsonElement extraData = default);
}

public sealed record ContractValidationError(string Field, string Message);

public sealed record PipelinePayload(
    byte[] Body,
    string ContentType,
    IReadOnlyDictionary<string, string> Attributes,
    IReadOnlyDictionary<string, object?>? RabbitMqAttributes = null);

public sealed record PipelineDispatchContext(
    string TaskId,
    string RuleId,
    string ImageId,
    JsonElement RoiFootprint,
    DateTimeOffset PhotoTime,
    string SensorType,
    string ImageUrl,
    int ImageWidth,
    int ImageHeight,
    double BestResolution,
    string SensorName,
    string? AreaOfInterest,
    string GridType,
    string GridUri)
{
    public string? LegId { get; init; }
    public string? PrevOverlayId { get; init; }
    public string? NextOverlayId { get; init; }

    /// <summary>
    /// The legacy Algo clock value from DateTime.Parse. Kept separately from the UTC timestamp
    /// used by ASD so formatting the mission date does not introduce a second conversion.
    /// </summary>
    public DateTime? OverlayPhotoTime { get; init; }
}
