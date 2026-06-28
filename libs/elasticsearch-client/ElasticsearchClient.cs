using System.Text.Json;
using Elasticsearch.Net;
using Nest;

namespace ImagingPipeline.ElasticsearchClient;

public interface IElasticsearchDocumentClient
{
    Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request);

    Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request);

    Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request);

    Task<TDocument?> GetAsync<TDocument>(
        string indexName,
        string id)
        where TDocument : class;

    Task IndexAsync<TDocument>(
        string indexName,
        string id,
        TDocument document,
        bool waitForRefresh = true)
        where TDocument : class;

    Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true)
        where TDocument : class;
}

public sealed class ElasticsearchDocumentClient : IElasticsearchDocumentClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IElasticClient _client;

    public ElasticsearchDocumentClient(IElasticClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
    {
        var body = ElasticsearchQueryJsonBuilder.BuildSearchBody(request);
        var response = await _client.LowLevel.SearchAsync<StringResponse>(
            request.IndexName,
            PostData.String(body),
            new SearchRequestParameters());

        EnsureValid(response, $"search index '{request.IndexName}'");
        return DeserializeSearchResponse<TDocument>(response.Body);
    }

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(
        ElasticsearchSensorSearchRequest request) =>
        SearchAsync<TDocument>(
            new ElasticsearchSearchRequest
            {
                IndexName = request.IndexName,
                From = request.From,
                Size = request.Size,
                SensorFilters =
                [
                    new ElasticsearchSensorFilter
                    {
                        SensorRootField = request.SensorRootField,
                        SensorName = request.SensorName,
                        Values = request.Values,
                        KeywordSuffix = request.KeywordSuffix
                    }
                ]
            });

    public Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(
        ElasticsearchGeoShapeSearchRequest request) =>
        SearchAsync<TDocument>(
            new ElasticsearchSearchRequest
            {
                IndexName = request.IndexName,
                From = request.From,
                Size = request.Size,
                GeoShapeFilters =
                [
                    new ElasticsearchGeoShapeFilter
                    {
                        Field = request.Field,
                        Shape = request.Shape,
                        Relation = request.Relation
                    }
                ]
            });

    public async Task<TDocument?> GetAsync<TDocument>(
        string indexName,
        string id)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);

        var response = await _client.GetAsync<TDocument>(
            id,
            descriptor => descriptor.Index(indexName));

        if (!response.Found)
        {
            return null;
        }

        EnsureValid(response, $"get document '{id}' from index '{indexName}'");
        return response.Source;
    }

    public async Task IndexAsync<TDocument>(
        string indexName,
        string id,
        TDocument document,
        bool waitForRefresh = true)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);
        ArgumentNullException.ThrowIfNull(document);

        var response = await _client.IndexAsync(
            document,
            descriptor =>
            {
                descriptor = descriptor.Index(indexName).Id(id);
                return waitForRefresh ? descriptor.Refresh(Refresh.WaitFor) : descriptor;
            });

        EnsureValid(response, $"index document '{id}' into index '{indexName}'");
    }

    public async Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);

        var response = await _client.DeleteAsync<TDocument>(
            id,
            descriptor =>
            {
                descriptor = descriptor.Index(indexName);
                return waitForRefresh ? descriptor.Refresh(Refresh.WaitFor) : descriptor;
            });

        if (response.Result == Result.NotFound)
        {
            return false;
        }

        EnsureValid(response, $"delete document '{id}' from index '{indexName}'");
        return true;
    }

    private static IReadOnlyList<TDocument> DeserializeSearchResponse<TDocument>(string body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var response = JsonSerializer.Deserialize<ElasticsearchSearchResponse<TDocument>>(body, JsonOptions);
        return response?.Hits?.Items
            .Select(hit => hit.Source)
            .Where(source => source is not null)
            .Cast<TDocument>()
            .ToArray() ?? [];
    }

    private static void ValidateIndexAndId(string indexName, string id)
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            throw new ArgumentException("Index name must not be empty.", nameof(indexName));
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Document id must not be empty.", nameof(id));
        }
    }

    private static void EnsureValid(IResponse response, string operation)
    {
        if (response.IsValid)
        {
            return;
        }

        var reason = response.ServerError?.Error?.Reason ??
            response.OriginalException?.Message ??
            response.DebugInformation;
        throw new ElasticsearchClientException($"Elasticsearch failed to {operation}: {reason}");
    }

    private static void EnsureValid(StringResponse response, string operation)
    {
        if (response.Success && response.HttpStatusCode is >= 200 and < 300)
        {
            return;
        }

        var reason = response.OriginalException?.Message ??
            response.Body ??
            response.DebugInformation;
        throw new ElasticsearchClientException($"Elasticsearch failed to {operation}: {reason}");
    }
}

public sealed class ElasticsearchClientException : Exception
{
    public ElasticsearchClientException(string message)
        : base(message)
    {
    }
}
