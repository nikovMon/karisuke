using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Contracts.Responses;

public sealed class BulkOperationResult
{
    [JsonPropertyName("successIds")]
    public List<string> SuccessIds { get; set; } = [];

    [JsonPropertyName("failedIds")]
    public List<BulkOperationFailure> FailedIds { get; set; } = [];
}

public sealed record BulkOperationFailure(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("reason")] string Reason);
