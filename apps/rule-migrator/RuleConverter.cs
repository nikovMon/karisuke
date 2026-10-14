using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.RuleMigrator;

/// <summary>
/// Converts a v1 rule to a v2 rule (SPEC-001 §8). <c>match.location</c> is left unset; it is derived
/// from the WKT once the rule passes validation. <c>area</c> has no v2 field and is dropped.
/// </summary>
internal static class RuleConverter
{
    public const string MigrationActor = "migration";

    public static PipelineRuleDocument Convert(RuleDto rule, int maxPhotoAgeDays) => new()
    {
        Id = rule.Id,
        RuleName = rule.RuleName,
        Description = rule.Description,
        IsActive = rule.IsActive,
        Match = new RuleMatchConditions
        {
            Sensors = rule.Sensors is { Count: > 0 } ? rule.Sensors : null,
            Resolution = new ResolutionRange { Min = rule.MinimumResolution, Max = rule.MaximumResolution },
            LocationWkt = rule.LocationWkt,
            MaxPhotoAgeDays = rule.IsPhotoOld == true ? maxPhotoAgeDays : null
        },
        RunParams = [.. (rule.TenantsInfo ?? []).Select(tenant => RunParams(tenant, rule.AlgorithmNames))],
        CreatedBy = MigrationActor,
        UpdatedBy = MigrationActor,
        CreationTime = rule.CreationTime,
        UpdateTime = rule.UpdateTime
    };

    private static JsonElement RunParams(TenantInfo tenant, List<AlgorithmName> algorithmNames) =>
        JsonSerializer.SerializeToElement(new
        {
            tenantId = tenant.TenantId,
            algorithmNames,
            tilingConfigs = tenant.TilingConfigs
        });
}
