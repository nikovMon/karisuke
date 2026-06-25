using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Contracts.Requests;

public sealed class RuleSensorUpdateRequest
{
    [JsonPropertyName("sensorName")]
    public string SensorName { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<string> Values { get; set; } = [];
}
