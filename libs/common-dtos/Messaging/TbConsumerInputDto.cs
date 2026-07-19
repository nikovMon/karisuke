using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

/// <summary>
/// The input message consumed by the TbConsumer component from the tile-builder output queue.
/// </summary>
public class TbConsumerInputDto
{
    [JsonPropertyName("frameMetadata")] public FrameMetadataDto FrameMetadata { get; set; } = new();
    [JsonPropertyName("modelMetadata")] public ModelMetadataDto ModelMetadata { get; set; } = new();
    [JsonPropertyName("focusedPxWkt")] public string FocusedPxWkt { get; set; } = string.Empty;
    [JsonPropertyName("missionMetadata")] public MissionMetadataDto MissionMetadata { get; set; } = new();
    [JsonPropertyName("requestId")] public string RequestId { get; set; } = string.Empty;
    [JsonPropertyName("taskId")] public string TaskId { get; set; } = string.Empty;
    [JsonPropertyName("tiles")] public List<TileBuilderTileOutput> Tiles { get; set; } = [];
}
