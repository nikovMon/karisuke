using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Rules.Models;

public sealed class SensorConfig
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("registrationQualities")]
    public List<RegistrationQuality> RegistrationQualities { get; set; } = [];

    [JsonPropertyName("gridTypes")]
    public List<string> GridTypes { get; set; } = [];
}
