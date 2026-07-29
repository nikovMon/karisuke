using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Gateway.Messages;

public sealed class GatewayOutputMessageDto : IValidatableObject
{
    [JsonPropertyName("taskId")]
    public required string TaskId { get; init; }

    [JsonPropertyName("ruleId")]
    public required string RuleId { get; init; }

    [JsonPropertyName("algorithmName")]
    public required IReadOnlyList<AlgorithmName> AlgorithmNames { get; init; }

    [JsonPropertyName("tenantId")]
    public required string TenantId { get; init; }

    [JsonPropertyName("tilingConfigs")]
    public required IReadOnlyList<TilingConfig> TilingConfigs { get; init; }

    [JsonPropertyName("imageId")]
    public required string ImageId { get; init; }

    [JsonPropertyName("roiFootprint")]
    public required JsonElement RoiFootprint { get; init; }

    [JsonPropertyName("photoTime")]
    public required DateTimeOffset PhotoTime { get; init; }

    [JsonPropertyName("sensorType")]
    public required string SensorType { get; init; }

    [JsonPropertyName("imageUrl")]
    public required string ImageUrl { get; init; }

    [JsonPropertyName("imageWidth")]
    public required int ImageWidth { get; init; }

    [JsonPropertyName("imageHeight")]
    public required int ImageHeight { get; init; }

    [JsonPropertyName("bestResolution")]
    public required double BestResolution { get; init; }

    [JsonPropertyName("sensorName")]
    public required string SensorName { get; init; }

    [JsonPropertyName("intersectionArea")]
    public required double IntersectionArea { get; init; }

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(TaskId))
        {
            yield return new ValidationResult("taskId cannot be empty.", [nameof(TaskId)]);
        }

        if (string.IsNullOrWhiteSpace(RuleId))
        {
            yield return new ValidationResult("ruleId cannot be empty.", [nameof(RuleId)]);
        }

        if (AlgorithmNames is null || AlgorithmNames.Count == 0)
        {
            yield return new ValidationResult(
                "algorithmName must contain at least one algorithm.",
                [nameof(AlgorithmNames)]);
        }
        else if (AlgorithmNames.Any(algorithm => !AlgorithmNameContract.IsDefined(algorithm)))
        {
            yield return new ValidationResult(
                "algorithmName contains an unsupported algorithm.",
                [nameof(AlgorithmNames)]);
        }
        else if (AlgorithmNames.Distinct().Count() != AlgorithmNames.Count)
        {
            yield return new ValidationResult(
                "algorithmName cannot contain duplicate algorithms.",
                [nameof(AlgorithmNames)]);
        }

        if (string.IsNullOrWhiteSpace(TenantId))
        {
            yield return new ValidationResult("tenantId cannot be empty.", [nameof(TenantId)]);
        }

        if (string.IsNullOrWhiteSpace(ImageId))
        {
            yield return new ValidationResult("imageId cannot be empty.", [nameof(ImageId)]);
        }

        if (RoiFootprint.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            yield return new ValidationResult("roiFootprint is required.", [nameof(RoiFootprint)]);
        }

        if (TilingConfigs is null || TilingConfigs.Count == 0)
        {
            yield return new ValidationResult("tilingConfigs must contain at least one entry.", [nameof(TilingConfigs)]);
        }
        else
        {
            foreach (var tilingConfig in TilingConfigs)
            {
                if (!TilingConfigValidator.IsValid(tilingConfig, out var tilingError))
                {
                    yield return new ValidationResult(tilingError, [nameof(TilingConfigs)]);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(SensorType))
        {
            yield return new ValidationResult("sensorType cannot be empty.", [nameof(SensorType)]);
        }

        if (string.IsNullOrWhiteSpace(ImageUrl))
        {
            yield return new ValidationResult("imageUrl cannot be empty.", [nameof(ImageUrl)]);
        }

        if (ImageWidth <= 0)
        {
            yield return new ValidationResult("imageWidth must be greater than zero.", [nameof(ImageWidth)]);
        }

        if (ImageHeight <= 0)
        {
            yield return new ValidationResult("imageHeight must be greater than zero.", [nameof(ImageHeight)]);
        }

        if (!double.IsFinite(BestResolution) || BestResolution <= 0)
        {
            yield return new ValidationResult(
                "bestResolution must be a positive finite number.",
                [nameof(BestResolution)]);
        }

        if (string.IsNullOrWhiteSpace(SensorName))
        {
            yield return new ValidationResult("sensorName cannot be empty.", [nameof(SensorName)]);
        }

        if (!double.IsFinite(IntersectionArea) || IntersectionArea < 0)
        {
            yield return new ValidationResult(
                "intersectionArea must be a non-negative finite number.",
                [nameof(IntersectionArea)]);
        }
    }
}
