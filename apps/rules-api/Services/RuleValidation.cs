using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;

namespace ImagingPipeline.Rules.Api.Services;

public static class RuleValidation
{
    public static IReadOnlyList<string> ValidateRule(RuleDto rule)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(rule, new ValidationContext(rule), results, validateAllProperties: true);
        return results.Select(result => result.ErrorMessage ?? "Rule is invalid.").ToArray();
    }

    public static IReadOnlyList<string> ValidateUpdate(UpdateRuleRequest request)
    {
        var errors = new List<string>();

        if (request.ProvidedFields.Count == 0)
        {
            errors.Add("At least one field must be provided.");
        }

        if (request.HasField("ruleName") && string.IsNullOrWhiteSpace(request.RuleName))
        {
            errors.Add("ruleName cannot be empty.");
        }

        if (request.HasField("algorithmName") && request.AlgorithmName is null)
        {
            errors.Add("algorithmName is required.");
        }

        if (request.HasField("minimumResolution") && request.MinimumResolution is null or <= 0)
        {
            errors.Add("minimumResolution must be greater than 0.");
        }

        if (request.HasField("maximumResolution") && request.MaximumResolution is null or <= 0)
        {
            errors.Add("maximumResolution must be greater than 0.");
        }

        if (request.HasField("isActive") && request.IsActive is null)
        {
            errors.Add("isActive cannot be null.");
        }

        if (request.HasField("sensors"))
        {
            AddSensorCollectionErrors(request.Sensors, errors);
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateSensorRequest(RuleSensorUpdateRequest request)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(request.SensorName))
        {
            errors.Add("sensorName cannot be empty.");
        }

        if (request.Values is null || request.Values.Count == 0 || request.Values.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("sensor values cannot be empty.");
        }

        if (request.Values is not null &&
            request.Values.Count != request.Values.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add("sensor values must be unique.");
        }

        return errors;
    }

    public static IReadOnlyList<string> ValidateIds(IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0)
        {
            return ["ids list cannot be empty."];
        }

        if (ids.Any(string.IsNullOrWhiteSpace))
        {
            return ["ids list cannot contain empty values."];
        }

        return [];
    }

    private static void AddSensorCollectionErrors(
        IReadOnlyDictionary<string, List<string>>? sensors,
        ICollection<string> errors)
    {
        if (sensors is null)
        {
            errors.Add("sensors cannot be null.");
            return;
        }

        if (sensors.Any(sensor => sensor.Value is null))
        {
            errors.Add("sensor value lists cannot be null.");
        }
    }
}
