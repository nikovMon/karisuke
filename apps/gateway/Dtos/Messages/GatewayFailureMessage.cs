using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Gateway.Dtos.Messages;

public sealed class GatewayFailureMessage
{
    [JsonPropertyName("failureId")]
    public string FailureId { get; set; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("failedAt")]
    public DateTimeOffset FailedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonPropertyName("service")]
    public string Service { get; set; } = string.Empty;

    [JsonPropertyName("stage")]
    public string Stage { get; set; } = string.Empty;

    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; set; } = string.Empty;

    [JsonPropertyName("errorMessage")]
    public string ErrorMessage { get; set; } = string.Empty;

    [JsonPropertyName("originalMessageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalMessageId { get; set; }

    [JsonPropertyName("imageId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ImageId { get; set; }

    [JsonPropertyName("originalRoutingKey")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? OriginalRoutingKey { get; set; }

    [JsonPropertyName("originalPayload")]
    public JsonElement OriginalPayload { get; set; }
}
