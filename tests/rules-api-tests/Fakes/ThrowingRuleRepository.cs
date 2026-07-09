using ImagingPipeline.ElasticsearchClient;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class ThrowingRuleRepository : IElasticsearchDocumentClient
{
    public Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class =>
        throw Failure();

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request)
        where TDocument : class =>
        throw Failure();

    public Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class =>
        throw Failure();

    public Task<TDocument?> GetAsync<TDocument>(string indexName, string id)
        where TDocument : class =>
        throw Failure();

    public Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw Failure();

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw Failure();

    public Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw Failure();

    public Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw Failure();

    public Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class =>
        throw Failure();

    private static ElasticsearchClientException Failure() =>
        new("Sensitive Elasticsearch failure details.");
}
