using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Messaging;

public class PolygonDto
{
    [JsonPropertyName("coordinates")] public double[][] Coordinates { get; set; } = Array.Empty<double[]>();
}
