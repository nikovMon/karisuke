using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class TileBuilderMetadataDto
{
    [JsonPropertyName("frameMetadata")]
    public FrameMetadataDto FrameMetadata { get; set; } = new();

    [JsonPropertyName("modelMetadata")]
    public ModelMetadataDto ModelMetadata { get; set; } = new();

    [JsonPropertyName("focusedPxWkt")]
    public string FocusedPxWkt { get; set; } = string.Empty;

    [JsonPropertyName("missionMetadata")]
    public MissionMetadataDto MissionMetadata { get; set; } = new();

    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;
}

public sealed class TbConsumerInputDto
{
    [JsonPropertyName("requestId")]
    public string RequestId { get; set; } = string.Empty;

    [JsonPropertyName("metadata")]
    public TileBuilderMetadataDto Metadata { get; set; } = new();

    [JsonPropertyName("tileUniqueMetadata")]
    public List<TileBuilderTileOutput> Tiles { get; set; } = [];
}
