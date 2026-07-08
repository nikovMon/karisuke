using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Processing.Messages;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class RuleValidator
{
    private readonly GatewayGeometryConverter _geometry;

    public RuleValidator(GatewayGeometryConverter geometry)
    {
        _geometry = geometry;
    }

    public IReadOnlyList<ActiveRule> BuildSnapshot(IReadOnlyList<RuleConfigDto> activeRules)
    {
        var snapshot = new List<ActiveRule>(activeRules.Count);

        foreach (var rule in activeRules)
        {
            if (!rule.IsActive)
            {
                continue;
            }

            Normalize(rule);

            var errors = new List<string>();
            var validationResults = new List<ValidationResult>();
            var context = new ValidationContext(rule);
            if (!Validator.TryValidateObject(rule, context, validationResults, validateAllProperties: true))
            {
                errors.AddRange(validationResults.Select(result => $"{RuleLabel(rule)}: {result.ErrorMessage}"));
            }

            ValidateTenantConfig(rule, errors);
            var geometry = ReadRuleGeometry(rule, errors);
            if (errors.Count == 0 && geometry is not null)
            {
                snapshot.Add(new ActiveRule(rule, geometry));
            }
        }

        return snapshot.ToArray();
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

    private NetTopologySuite.Geometries.Geometry? ReadRuleGeometry(RuleConfigDto rule, List<string> errors)
    {
        try
        {
            return _geometry.ReadRuleGeometry(rule);
        }
        catch (Exception ex)
        {
            errors.Add($"{RuleLabel(rule)}: {ex.Message}");
            return null;
        }
    }

    private static string RuleLabel(RuleConfigDto rule) =>
        string.IsNullOrWhiteSpace(rule.Id) ? rule.RuleName : rule.Id;
}
