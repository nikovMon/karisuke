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

        if (request.HasField("algorithmName") &&
            (request.AlgorithmNames is null || request.AlgorithmNames.Count == 0))
        {
            errors.Add("algorithmName must contain at least one algorithm.");
        }

        if (request.AlgorithmNames is { Count: > 0 } &&
            request.AlgorithmNames.Any(value => !AlgorithmNameContract.IsDefined(value)))
        {
            errors.Add("algorithmName values must be valid algorithms.");
        }

        if (request.AlgorithmNames is { Count: > 0 } &&
            request.AlgorithmNames.Count != request.AlgorithmNames.Distinct().Count())
        {
            errors.Add("algorithmName values must be unique.");
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

        if (request.HasField("locationWkt"))
        {
            if (string.IsNullOrWhiteSpace(request.LocationWkt))
            {
                errors.Add("locationWkt is required.");
            }
            else if (!RuleGeometry.ConvertWktToGeoJson(
                request.LocationWkt,
                out _,
                out var geometryError))
            {
                errors.Add(geometryError!);
            }
        }

        if (request.HasField("locationGeoJson") && !request.HasField("locationWkt"))
        {
            errors.Add("locationGeoJson is generated from locationWkt and cannot be updated directly.");
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

        var hasQualities = request.RegistrationQualities is { Count: > 0 };
        var hasGridTypes = request.GridTypes is { Count: > 0 };

        if (!hasQualities && !hasGridTypes)
        {
            errors.Add("At least one of registrationQualities or gridTypes must be provided with values.");
        }

        if (request.RegistrationQualities is not null && request.RegistrationQualities.Any(value => !Enum.IsDefined(value)))
        {
            errors.Add("registrationQualities values must be valid registration qualities.");
        }

        if (request.RegistrationQualities is not null &&
            request.RegistrationQualities.Count != request.RegistrationQualities.Distinct().Count())
        {
            errors.Add("registrationQualities values must be unique.");
        }

        if (request.GridTypes is not null && request.GridTypes.Any(string.IsNullOrWhiteSpace))
        {
            errors.Add("gridTypes values must be non-empty strings.");
        }

        if (request.GridTypes is not null &&
            request.GridTypes.Count != request.GridTypes.Distinct(StringComparer.Ordinal).Count())
        {
            errors.Add("gridTypes values must be unique.");
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
        List<SensorConfig>? sensors,
        ICollection<string> errors)
    {
        if (sensors is null)
        {
            errors.Add("sensors cannot be null.");
            return;
        }

        foreach (var error in SensorConfig.ValidateCollection(sensors))
        {
            errors.Add(error);
        }
    }
}
