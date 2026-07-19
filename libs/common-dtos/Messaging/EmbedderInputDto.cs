using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

/// <summary>
/// Final envelope published to the Embedder queue.
/// Metadata fields are at the top level alongside the embedder input.
/// </summary>
public class EmbedderInputDto
{
    [JsonPropertyName("frameMetadata")] public FrameMetadataDto FrameMetadata { get; set; } = new();
    [JsonPropertyName("modelMetadata")] public ModelMetadataDto ModelMetadata { get; set; } = new();
    [JsonPropertyName("focusedPxWkt")] public string FocusedPxWkt { get; set; } = string.Empty;
    [JsonPropertyName("missionMetadata")] public MissionMetadataDto MissionMetadata { get; set; } = new();
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("embedder_input")]
    public EmbedderInput EmbedderInput { get; set; } = new();
}
