using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class FrameGeneralDto
{
    [JsonPropertyName("imageFileURI")] 
    public string ImageFileUri { get; set; } = string.Empty;
    [JsonPropertyName("id")] 
    public string Id { get; set; } = string.Empty;
}
