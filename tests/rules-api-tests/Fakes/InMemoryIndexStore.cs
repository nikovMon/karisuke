using System.Text.Json;
using System.Text.Json.Nodes;
using ImagingPipeline.ElasticsearchClient;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

/// <summary>
/// Keeps documents as JSON per index, so tests can tell which index a call reached.
/// </summary>
internal sealed class InMemoryIndexStore : IElasticsearchDocumentClient
{
    private readonly Dictionary<string, Dictionary<string, JsonObject>> _indexes = new(StringComparer.Ordinal);

    public int CallCount { get; private set; }

    public void Add<TDocument>(string indexName, string id, TDocument document) =>
        Index(indexName)[id] = JsonSerializer.SerializeToNode(document)!.AsObject();

    public Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        CallCount++;
        return Task.FromResult(Index(indexName).TryGetValue(id, out var source)
            ? new ElasticsearchDocument<TDocument>(id, source.Deserialize<TDocument>()!)
            : null);
    }

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        CallCount++;
        IReadOnlyList<ElasticsearchDocument<TDocument>> documents = Index(request.IndexName)
            .Where(entry => !request.ExcludedIds.Contains(entry.Key, StringComparer.Ordinal))
            .Where(entry => request.TermFilters.All(filter => Matches(entry.Value, filter)))
            .Skip(request.From)
            .Take(request.Size)
            .Select(entry => new ElasticsearchDocument<TDocument>(entry.Key, entry.Value.Deserialize<TDocument>()!))
            .ToArray();
        return Task.FromResult(documents);
    }

    public Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<TDocument?> GetAsync<TDocument>(string indexName, string id)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();

    public Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();

    public Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();

    private Dictionary<string, JsonObject> Index(string indexName)
    {
        if (!_indexes.TryGetValue(indexName, out var index))
        {
            index = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
            _indexes[indexName] = index;
        }

        return index;
    }

    private static bool Matches(JsonObject source, ElasticsearchTermFilter filter)
    {
        var field = filter.Field.EndsWith(".keyword", StringComparison.Ordinal)
            ? filter.Field[..^".keyword".Length]
            : filter.Field;
        return source[field] is { } value &&
               JsonNode.DeepEquals(value, JsonSerializer.SerializeToNode(filter.Value));
    }
}
