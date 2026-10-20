using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Historical;

/// <summary>
/// One historical overlay and the saved rule snapshots selected for this run. The gateway
/// processes only these targets; it never broadens them using its live rule cache.
/// </summary>
public sealed class HistoricalOverlayEnvelope
{
    public const string MessageKind = "historical-auto-run";
    public const int CurrentVersion = 1;

    [JsonPropertyName("kind"), JsonRequired]
    public string Kind { get; init; } = MessageKind;

    [JsonPropertyName("version"), JsonRequired]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("runId"), JsonRequired]
    public string RunId { get; init; } = string.Empty;

    [JsonPropertyName("overlay"), JsonRequired]
    public JsonElement Overlay { get; init; }

    [JsonPropertyName("targets"), JsonRequired]
    public List<HistoricalRuleTarget> Targets { get; init; } = [];
}

public sealed class HistoricalRuleTarget
{
    [JsonPropertyName("pipelineId"), JsonRequired]
    public string PipelineId { get; init; } = string.Empty;

    [JsonPropertyName("rule"), JsonRequired]
    public PipelineRuleDocument Rule { get; init; } = null!;
}
