using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperResponseDto
{
    [JsonPropertyName("coordinates")]
    public IReadOnlyList<IReadOnlyList<double>> Coordinates { get; set; } = [];
}
