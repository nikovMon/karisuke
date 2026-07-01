using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Application.Messages;

namespace ImagingPipeline.Gateway.Application.Rules;

public sealed class RuleValidator
{
    private readonly GeometryExtractor _geometryExtractor;

    public RuleValidator(GeometryExtractor geometryExtractor)
    {
        _geometryExtractor = geometryExtractor;
    }

    public void ValidateSnapshot(IReadOnlyList<RuleConfigDto> activeRules)
    {
        var errors = new List<string>();

        foreach (var rule in activeRules)
        {
            Normalize(rule);

            var validationResults = new List<ValidationResult>();
            var context = new ValidationContext(rule);
            if (!Validator.TryValidateObject(rule, context, validationResults, validateAllProperties: true))
            {
                errors.AddRange(validationResults.Select(result => $"{RuleLabel(rule)}: {result.ErrorMessage}"));
            }

            ValidateTenantConfig(rule, errors);
            ValidateRuleGeometry(rule, errors);
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(
                "Invalid active rule snapshot: " + string.Join("; ", errors.Take(20)));
        }
    }

    private static void Normalize(RuleConfigDto rule)
    {
        rule.Sensors ??= new Dictionary<string, List<string>>(StringComparer.Ordinal);
        rule.TenantsInfo ??= [];

        foreach (var sensorKey in rule.Sensors.Keys.ToArray())
        {
            rule.Sensors[sensorKey] ??= [];
        }

        foreach (var tenant in rule.TenantsInfo)
        {
            if (tenant is not null)
            {
                tenant.TilingConfigs ??= [];
            }
        }
    }

    private static void ValidateTenantConfig(RuleConfigDto rule, List<string> errors)
    {
        foreach (var tenant in rule.TenantsInfo)
        {
            if (tenant is null)
            {
                errors.Add($"{RuleLabel(rule)}: tenant cannot be null");
                continue;
            }

            if (string.IsNullOrWhiteSpace(tenant.TenantId))
            {
                errors.Add($"{RuleLabel(rule)}: tenantId cannot be empty");
            }

            if (tenant.TilingConfigs is null || tenant.TilingConfigs.Count == 0)
            {
                errors.Add($"{RuleLabel(rule)}: tilingConfigs cannot be empty");
                continue;
            }

            foreach (var tiling in tenant.TilingConfigs)
            {
                if (tiling is null)
                {
                    errors.Add($"{RuleLabel(rule)}: tilingConfig cannot be null");
                    continue;
                }

                if (tiling.TileSizeWidth <= 0 || tiling.TileSizeHeight <= 0)
                {
                    errors.Add($"{RuleLabel(rule)}: tilingConfig tile dimensions must be greater than 0");
                }

                if (tiling.TileOverlapWidth < 0 || tiling.TileOverlapHeight < 0)
                {
                    errors.Add($"{RuleLabel(rule)}: tilingConfig tile overlaps cannot be negative");
                }
            }
        }
    }

    private void ValidateRuleGeometry(RuleConfigDto rule, List<string> errors)
    {
        try
        {
            _geometryExtractor.ReadRuleGeometry(rule);
        }
        catch (Exception ex)
        {
            errors.Add($"{RuleLabel(rule)}: {ex.Message}");
        }
    }

    private static string RuleLabel(RuleConfigDto rule) =>
        string.IsNullOrWhiteSpace(rule.Id) ? rule.RuleName : rule.Id;
}
