using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public sealed class ModelMetadataDto
{
    [JsonPropertyName("overlap_height")]
    public int OverlapHeight { get; set; }

    [JsonPropertyName("tb_crop_size_y")]
    public int TbCropSizeY { get; set; }

    [JsonPropertyName("overlap_width")]
    public int OverlapWidth { get; set; }

    [JsonPropertyName("tb_crop_size_x")]
    public int TbCropSizeX { get; set; }
}
