using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

/// <summary>
/// Final envelope published to the Embedder queue.
/// Retained transport fields are at the top level alongside the embedder input.
/// </summary>
public class EmbedderInputDto
{
    [JsonPropertyName("frameMetadata")] 
    public FrameMetadataDto FrameMetadata { get; set; } = new();
    
    [JsonPropertyName("taskId")]
    public string TaskId { get; set; } = string.Empty;

    [JsonPropertyName("url")]
    public string ImageUrl { get; set; } = string.Empty;

    [JsonPropertyName("s3Uri")]
    public string S3Uri { get; set; } = string.Empty;

    [JsonPropertyName("embedder_input")]
    public EmbedderInputPayload EmbedderInput { get; set; } = new();
}
