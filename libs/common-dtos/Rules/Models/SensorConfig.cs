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

    public static IEnumerable<string> ValidateCollection(IReadOnlyList<SensorConfig> sensors)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < sensors.Count; i++)
        {
            var sensor = sensors[i];
            if (sensor is null)
            {
                yield return $"sensors[{i}] cannot be null";
                continue;
            }

            if (string.IsNullOrWhiteSpace(sensor.Name))
            {
                yield return $"sensors[{i}].name cannot be empty";
            }
            else if (!names.Add(sensor.Name))
            {
                yield return $"Duplicate sensor name '{sensor.Name}'";
            }

            if (sensor.RegistrationQualities is not null &&
                sensor.RegistrationQualities.Any(value => !Enum.IsDefined(value)))
            {
                yield return $"sensors[{i}].registrationQualities contains invalid values";
            }

            if (sensor.GridTypes is not null &&
                sensor.GridTypes.Any(string.IsNullOrWhiteSpace))
            {
                yield return $"sensors[{i}].gridTypes contains empty or whitespace values";
            }

            if (sensor is not null &&
                sensor.RegistrationQualities is { Count: 0 } or null &&
                sensor.GridTypes is { Count: 0 } or null)
            {
                yield return $"sensors[{i}] must have at least one registrationQuality or gridType";
            }
        }
    }
}
