using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>Adds or removes one sensor's match values, for both v1 and v2 rules.</summary>
internal static class RuleSensors
{
    public static void Add(List<SensorConfig> sensors, RuleSensorUpdateRequest request)
    {
        var sensor = sensors.FirstOrDefault(s =>
            string.Equals(s.Name, request.SensorName, StringComparison.Ordinal));

        if (sensor is null)
        {
            sensor = new SensorConfig { Name = request.SensorName };
            sensors.Add(sensor);
        }

        if (request.RegistrationQualities is not null)
        {
            foreach (var value in request.RegistrationQualities)
            {
                if (!sensor.RegistrationQualities.Contains(value))
                {
                    sensor.RegistrationQualities.Add(value);
                }
            }
        }

        if (request.GridTypes is not null)
        {
            foreach (var value in request.GridTypes)
            {
                if (!sensor.GridTypes.Contains(value, StringComparer.Ordinal))
                {
                    sensor.GridTypes.Add(value);
                }
            }
        }
    }

    public static void Remove(List<SensorConfig> sensors, RuleSensorUpdateRequest request)
    {
        var sensor = sensors.FirstOrDefault(s =>
            string.Equals(s.Name, request.SensorName, StringComparison.Ordinal));

        if (sensor is null)
        {
            return;
        }

        if (request.RegistrationQualities is not null)
        {
            sensor.RegistrationQualities.RemoveAll(value => request.RegistrationQualities.Contains(value));
        }

        if (request.GridTypes is not null)
        {
            sensor.GridTypes.RemoveAll(value =>
                request.GridTypes.Contains(value, StringComparer.Ordinal));
        }

        // Remove the sensor entry entirely when both criteria lists are empty.
        // An empty entry would mean "match all" for this sensor, which is unlikely
        // to be the intent after explicitly removing all filter values.
        if (sensor.RegistrationQualities.Count == 0 && sensor.GridTypes.Count == 0)
        {
            sensors.Remove(sensor);
        }
    }
}
