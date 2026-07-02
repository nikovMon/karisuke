using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.Repositories;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class InMemoryRuleRepository : IRuleRepository
{
    private readonly Dictionary<string, RuleDto> _rules = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RuleDto> SavedRules => _rules.Values;

    public void Clear() => _rules.Clear();

    public void Add(RuleDto rule) => _rules[rule.Id] = Clone(rule);

    public Task<IReadOnlyList<RuleDto>> GetAllAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RuleDto> rules = _rules.Values
            .Where(rule => !isActive.HasValue || rule.IsActive == isActive.Value)
            .Skip(from)
            .Take(size)
            .Select(Clone)
            .ToArray();

        return Task.FromResult(rules);
    }

    public Task<IReadOnlyList<string>> GetNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<string> names = _rules.Values
            .Where(rule => !isActive.HasValue || rule.IsActive == isActive.Value)
            .Skip(from)
            .Take(size)
            .Select(rule => rule.RuleName)
            .ToArray();

        return Task.FromResult(names);
    }

    public Task<RuleDto?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rules.TryGetValue(id, out var rule) ? Clone(rule) : null);
    }

    public Task<RuleDto?> GetByNameAsync(
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        var rule = _rules.Values.FirstOrDefault(item =>
            string.Equals(item.RuleName, ruleName, StringComparison.Ordinal));

        return Task.FromResult(rule is null ? null : Clone(rule));
    }

    public Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        var exists = _rules.Values.Any(rule =>
            string.Equals(rule.RuleName, ruleName, StringComparison.Ordinal) &&
            !string.Equals(rule.Id, excludingId, StringComparison.Ordinal));

        return Task.FromResult(exists);
    }

    public Task SaveAsync(RuleDto rule, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rule.Id))
        {
            rule.Id = Guid.NewGuid().ToString();
        }

        _rules[rule.Id] = Clone(rule);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_rules.Remove(id));
    }

    private static RuleDto Clone(RuleDto rule) =>
        new()
        {
            Id = rule.Id,
            RuleName = rule.RuleName,
            Description = rule.Description,
            AlgorithmName = rule.AlgorithmName,
            Sensors = rule.Sensors.ToDictionary(
                item => item.Key,
                item => item.Value.ToList(),
                StringComparer.Ordinal),
            IsActive = rule.IsActive,
            TenantsInfo = rule.TenantsInfo.Select(tenant => new TenantInfo
            {
                TenantId = tenant.TenantId,
                TilingConfigs = tenant.TilingConfigs.Select(tiling => new TilingConfig
                {
                    TileSizeWidth = tiling.TileSizeWidth,
                    TileSizeHeight = tiling.TileSizeHeight,
                    TileOverlapWidth = tiling.TileOverlapWidth,
                    TileOverlapHeight = tiling.TileOverlapHeight
                }).ToList()
            }).ToList(),
            MinimumResolution = rule.MinimumResolution,
            MaximumResolution = rule.MaximumResolution,
            Area = rule.Area,
            LocationWkt = rule.LocationWkt,
            LocationGeoJson = rule.LocationGeoJson,
            IsPhotoOld = rule.IsPhotoOld,
            CreationTime = rule.CreationTime,
            UpdateTime = rule.UpdateTime
        };
}
