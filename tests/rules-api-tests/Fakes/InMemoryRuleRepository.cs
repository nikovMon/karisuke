using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class InMemoryRuleRepository : IElasticsearchDocumentClient
{
    private readonly Dictionary<string, RuleDto> _rules = new(StringComparer.Ordinal);

    public IReadOnlyCollection<RuleDto> SavedRules => _rules.Values;

    public void Clear() => _rules.Clear();

    public void Add(RuleDto rule) => _rules[rule.Id] = Clone(rule);

    public Task<RuleDto?> GetByIdAsync(string id)
    {
        return Task.FromResult(_rules.TryGetValue(id, out var rule) ? Clone(rule) : null);
    }

    public Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class
    {
        IReadOnlyList<TDocument> documents = SearchDocuments<TDocument>(request)
            .Select(document => document.Source)
            .ToArray();

        return Task.FromResult(documents);
    }

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request)
        where TDocument : class =>
        throw new NotSupportedException();

    public Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class =>
        throw new NotSupportedException();

    public async Task<TDocument?> GetAsync<TDocument>(string indexName, string id)
        where TDocument : class
    {
        var document = await GetDocumentAsync<TDocument>(indexName, id);
        return document?.Source;
    }

    public Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        if (!_rules.TryGetValue(id, out var rule))
        {
            return Task.FromResult<ElasticsearchDocument<TDocument>?>(null);
        }

        var source = ConvertRule<TDocument>(rule);
        return Task.FromResult<ElasticsearchDocument<TDocument>?>(new ElasticsearchDocument<TDocument>(id, source));
    }

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        Task.FromResult(SearchDocuments<TDocument>(request));

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw new NotSupportedException();

    public Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        if (document is not RuleDto rule)
        {
            throw new NotSupportedException();
        }

        var documentId = string.IsNullOrWhiteSpace(id) ? Guid.NewGuid().ToString() : id;
        var stored = Clone(rule);
        stored.Id = documentId;
        _rules[documentId] = stored;
        return Task.FromResult(documentId);
    }

    public Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        Task.FromResult(_rules.Remove(id));

    private IReadOnlyList<ElasticsearchDocument<TDocument>> SearchDocuments<TDocument>(
        ElasticsearchSearchRequest request)
        where TDocument : class
    {
        var rules = _rules.Values
            .Where(rule => Matches(rule, request))
            .Skip(request.From)
            .Take(request.Size)
            .Select(rule => new ElasticsearchDocument<TDocument>(rule.Id, ConvertRule<TDocument>(rule)))
            .ToArray();

        return rules;
    }

    private static bool Matches(RuleDto rule, ElasticsearchSearchRequest request)
    {
        if (request.ExcludedIds.Contains(rule.Id, StringComparer.Ordinal))
        {
            return false;
        }

        foreach (var filter in request.TermFilters)
        {
            if (filter.Field == "isActive" &&
                filter.Value is bool isActive &&
                rule.IsActive != isActive)
            {
                return false;
            }

            if (filter.Field == "ruleName.keyword" &&
                !string.Equals(rule.RuleName, filter.Value?.ToString(), StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static TDocument ConvertRule<TDocument>(RuleDto rule)
        where TDocument : class
    {
        if (typeof(TDocument) == typeof(RuleDto))
        {
            return (TDocument)(object)Clone(rule);
        }

        var document = Activator.CreateInstance<TDocument>();
        typeof(TDocument).GetProperty("RuleName")?.SetValue(document, rule.RuleName);
        return document;
    }

    private static RuleDto Clone(RuleDto rule) =>
        new()
        {
            Id = rule.Id,
            RuleName = rule.RuleName,
            Description = rule.Description,
            AlgorithmNames = rule.AlgorithmNames.ToList(),
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
