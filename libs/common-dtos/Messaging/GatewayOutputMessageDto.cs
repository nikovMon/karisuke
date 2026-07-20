using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Messaging;

public sealed class GatewayOutputMessageDto : IValidatableObject
{
    [JsonPropertyName("taskId")]
    public string TaskId { get; init; } = string.Empty;

    [JsonPropertyName("ruleId")]
    public string RuleId { get; init; } = string.Empty;

    [JsonPropertyName("algorithmName")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AlgorithmName AlgorithmName { get; init; }

    [JsonPropertyName("tenantId")]
    public string TenantId { get; init; } = string.Empty;

    [JsonPropertyName("tilingConfigs")]
    public IReadOnlyList<TilingConfig> TilingConfigs { get; init; } = [];

    [JsonPropertyName("imageId")]
    public string ImageId { get; init; } = string.Empty;

    [JsonPropertyName("roiFootprint")]
    public JsonElement RoiFootprint { get; init; }

    [JsonPropertyName("photoTime")]
    public DateTimeOffset PhotoTime { get; init; }

    [JsonPropertyName("sensorType")]
    public string SensorType { get; init; } = string.Empty;

    [JsonPropertyName("imageUrl")]
    public string ImageUrl { get; init; } = string.Empty;

    [JsonPropertyName("imageWidth")]
    public int ImageWidth { get; init; }

    [JsonPropertyName("imageHeight")]
    public int ImageHeight { get; init; }

    [JsonPropertyName("resolutionMPerPx")]
    public double ResolutionMPerPx { get; init; }

    [JsonPropertyName("sensorName")]
    public string SensorName { get; init; } = string.Empty;

    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (string.IsNullOrWhiteSpace(RuleId))
        {
            yield return new ValidationResult("ruleId cannot be empty.", [nameof(RuleId)]);
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

        if (TilingConfigs.Count == 0)
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
    }
}
