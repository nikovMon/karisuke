using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Gateway.Messages;

public sealed class GatewayOutputPayload
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
    public DateTimeOffset? PhotoTime { get; init; }
}
