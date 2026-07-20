using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class FrameMetadataDto
{
    [JsonPropertyName("general")] 
    public FrameGeneralDto General { get; set; } = new();
}
