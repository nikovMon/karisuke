using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class I2GByIdRequestDto
{
    [JsonPropertyName("coordinates")]
    public IReadOnlyList<IReadOnlyList<double>> Coordinates { get; set; } = [];
}
