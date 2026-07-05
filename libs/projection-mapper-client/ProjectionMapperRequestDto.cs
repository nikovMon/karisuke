using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperRequestDto
{
    [JsonPropertyName("groundPoints")]
    public JsonElement GroundPoints { get; set; }
}
