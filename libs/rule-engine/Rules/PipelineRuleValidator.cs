using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.PipelineContracts;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.RuleEngine.Rules;

/// <summary>
/// Checks one rule document against the document rules and its pipeline's contract. The Rules API
/// runs it on write and the gateway on load, so a rule that saves cleanly is never rejected later.
/// </summary>
public static class PipelineRuleValidator
{
    public static PipelineRuleValidation Validate(PipelineRuleDocument rule, IPipelineContract contract)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(contract);

        var errors = ValidateDocument(rule)
            .Concat(ValidateRunParams(rule, contract))
            .Concat(contract.ValidateRuleLocation(rule.Match?.LocationWkt).Select(error => $"{error.Field}: {error.Message}"))
            .ToList();
        var geometry = ReadGeometry(rule.Match?.LocationWkt, errors);
        return new PipelineRuleValidation(errors, geometry);
    }

    private static IEnumerable<string> ValidateDocument(PipelineRuleDocument rule)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(rule, new ValidationContext(rule), results, validateAllProperties: true);
        return results.Select(result => result.ErrorMessage ?? "Rule validation failed.");
    }

    private static IEnumerable<string> ValidateRunParams(PipelineRuleDocument rule, IPipelineContract contract)
    {
        var runParams = rule.RunParams ?? [];
        for (var index = 0; index < runParams.Count; index++)
        {
            foreach (var error in contract.ValidateRunParams(runParams[index]))
            {
                yield return $"runParams[{index}].{error.Field}: {error.Message}";
            }
        }
    }

    private static Geometry? ReadGeometry(string? locationWkt, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(locationWkt))
        {
            return null;
        }

        try
        {
            return GeometryUtilities.ReadWkt(locationWkt);
        }
        catch (Exception ex) when (ex is ArgumentException or GeometryValidationException or ParseException)
        {
            errors.Add("match.locationWkt must contain a valid, non-empty WKT geometry");
            return null;
        }
    }
}

/// <summary>
/// The validation errors of one rule, and its parsed location when it has a readable one.
/// </summary>
public sealed record PipelineRuleValidation(IReadOnlyList<string> Errors, Geometry? Geometry)
{
    public bool IsValid => Errors.Count == 0;
}
