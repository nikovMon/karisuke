using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperRequestDto
{
    [JsonPropertyName("groundPoints")]
    public IReadOnlyList<IReadOnlyList<double>> GroundPoints { get; set; } = [];
}
