using System.Text.Json;
using System.Text.Json.Nodes;
using ImagingPipeline.ElasticsearchClient;
using Nest;

namespace ImagingPipeline.RuleMigrator.Tests;

/// <summary>Stores raw JSON sources per index and implements only what the migration calls.</summary>
internal sealed class FakeDocumentClient : IElasticsearchDocumentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly Dictionary<string, List<(string Id, string Json)>> _indexes = new(StringComparer.Ordinal);

    public HashSet<string> FailingSaveIds { get; } = [];

    public List<(string Index, string Id, JsonObject Source)> Saved { get; } = [];

    public void Add(string indexName, string id, object source) =>
        Documents(indexName).Add((id, source as string ?? JsonSerializer.Serialize(source, JsonOptions)));

    public void CreateIndex(string indexName) => Documents(indexName);

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        if (!_indexes.TryGetValue(request.IndexName, out var documents))
        {
            throw new ElasticsearchClientException($"index '{request.IndexName}' not found");
        }

        IReadOnlyList<ElasticsearchDocument<TDocument>> page = documents
            .Skip(request.From)
            .Take(request.Size)
            .Select(document => new ElasticsearchDocument<TDocument>(
                document.Id,
                JsonSerializer.Deserialize<TDocument>(document.Json, JsonOptions)!))
            .ToArray();
        return Task.FromResult(page);
    }

    public Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        if (FailingSaveIds.Contains(id!))
        {
            throw new ElasticsearchClientException($"save '{id}' failed");
        }

        Saved.Add((indexName, id!, JsonSerializer.SerializeToNode(document, JsonOptions)!.AsObject()));
        return Task.FromResult(id!);
    }

    private List<(string Id, string Json)> Documents(string indexName)
    {
        if (!_indexes.TryGetValue(indexName, out var documents))
        {
            _indexes[indexName] = documents = [];
        }

        return documents;
    }

    public Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class => throw new NotSupportedException();

    public Task<TDocument?> GetAsync<TDocument>(string indexName, string id)
        where TDocument : class => throw new NotSupportedException();

    public Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();

    public Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class => throw new NotSupportedException();
}
