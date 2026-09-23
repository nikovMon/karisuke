using System.Text.Json;

namespace ImagingPipeline.PipelineContracts;

public interface IPipelineContract
{
    string ContractId { get; }

    IReadOnlyList<ContractValidationError> ValidateRunParams(JsonElement runParams);

    /// <summary>
    /// Builds the downstream payload. Optional extra data is a JSON object carried under
    /// the body's extraData property; it does not override contract fields or become headers.
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
    string GridUri);
