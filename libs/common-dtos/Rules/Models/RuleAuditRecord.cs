using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

/// <summary>
/// One change to a pipeline rule, appended to the audit index and never updated. States are full
/// rule snapshots; <see cref="PreviousState"/> is absent for a creation and <see cref="NewState"/>
/// for a deletion.
/// </summary>
public sealed class RuleAuditRecord
{
    [JsonPropertyName("pipelineId")]
    public string PipelineId { get; set; } = string.Empty;

    [JsonPropertyName("ruleId")]
    public string RuleId { get; set; } = string.Empty;

    [JsonPropertyName("changeType")]
    public RuleChangeType ChangeType { get; set; }

    [JsonPropertyName("previousState")]
    public JsonElement? PreviousState { get; set; }

    [JsonPropertyName("newState")]
    public JsonElement? NewState { get; set; }

    [JsonPropertyName("changedFields")]
    public IReadOnlyList<string>? ChangedFields { get; set; }

    /// <summary>Null until the Rules API authenticates its callers.</summary>
    [JsonPropertyName("changedBy")]
    public string? ChangedBy { get; set; }

    [JsonPropertyName("changedAt")]
    public DateTimeOffset ChangedAt { get; set; }

    [JsonPropertyName("reason")]
    public string? Reason { get; set; }

    /// <summary>The ID of the HTTP request that made the change.</summary>
    [JsonPropertyName("requestId")]
    public string? RequestId { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter<RuleChangeType>))]
public enum RuleChangeType
{
    [JsonStringEnumMemberName("created")]
    Created = 1,

    [JsonStringEnumMemberName("updated")]
    Updated = 2,

    [JsonStringEnumMemberName("activated")]
    Activated = 3,

    [JsonStringEnumMemberName("deactivated")]
    Deactivated = 4,

    [JsonStringEnumMemberName("deleted")]
    Deleted = 5
}

public static class RuleChangeTypeExtensions
{
    /// <summary>The value stored in audit records, such as <c>deactivated</c>.</summary>
    public static string Value(this RuleChangeType changeType) => changeType switch
    {
        RuleChangeType.Created => "created",
        RuleChangeType.Updated => "updated",
        RuleChangeType.Activated => "activated",
        RuleChangeType.Deactivated => "deactivated",
        RuleChangeType.Deleted => "deleted",
        _ => throw new ArgumentOutOfRangeException(nameof(changeType), changeType, null)
    };
}
