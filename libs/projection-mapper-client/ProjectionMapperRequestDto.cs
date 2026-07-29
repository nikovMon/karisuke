using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperRequestDto
{
    [JsonPropertyName("coordinates")]
    public IReadOnlyList<IReadOnlyList<double>> Coordinates { get; set; } = [];
}
