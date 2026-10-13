using ImagingPipeline.Common.Dtos.Rules.Requests;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>Request checks shared by the bulk and sensor rule routes.</summary>
public static class RuleValidation
{
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
}
