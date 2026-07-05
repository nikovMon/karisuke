using System.Text.Json.Serialization;

namespace ImagingPipeline.TbPublisher.Dtos.Outbound;

public sealed class FrameMetadataDto
{
    [JsonPropertyName("general")]
    public FrameGeneralDto General { get; set; } = new();
}

public sealed class FrameGeneralDto
{
    [JsonPropertyName("imageFileURI")]
    public string ImageFileUri { get; set; } = string.Empty;

    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;
}
