using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Common.Dtos.Rules.Requests;

public sealed class RuleSensorUpdateRequest
{
    [JsonPropertyName("sensorName")]
    public string SensorName { get; set; } = string.Empty;

    [JsonPropertyName("values")]
    public List<RegistrationQuality> Values { get; set; } = [];
}
