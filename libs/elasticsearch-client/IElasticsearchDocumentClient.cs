namespace ImagingPipeline.ElasticsearchClient;

public interface IElasticsearchDocumentClient
{
    Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(
        ElasticsearchSensorSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(
        ElasticsearchGeoShapeSearchRequest request,
        CancellationToken cancellationToken = default);

    Task<TDocument?> GetAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task IndexAsync<TDocument>(
        string indexName,
        string id,
        TDocument document,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class;
}
