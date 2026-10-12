using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Responses;

public sealed class BulkOperationResult
{
    [JsonPropertyName("successIds")]
    public List<string> SuccessIds { get; set; } = [];

    [JsonPropertyName("failedIds")]
    public List<BulkOperationFailure> FailedIds { get; set; } = [];

    public RuleOperationResult<BulkOperationResult> ToOperationResult()
    {
        if (FailedIds.Count == 0)
        {
            return RuleOperationResult<BulkOperationResult>.Success(this);
        }

        return SuccessIds.Count == 0
            ? RuleOperationResult<BulkOperationResult>.AllFailed(this)
            : RuleOperationResult<BulkOperationResult>.PartialSuccess(this);
    }
}

public sealed record BulkOperationFailure(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("reason")] string Reason);
