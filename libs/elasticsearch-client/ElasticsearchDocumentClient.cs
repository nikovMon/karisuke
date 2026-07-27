using System.Net;
using System.Reflection;
using System.Text.Json;
using Elasticsearch.Net;
using Nest;

namespace ImagingPipeline.ElasticsearchClient;

public interface IElasticsearchDocumentClient
{
    Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class;

    Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(ElasticsearchSensorSearchRequest request)
        where TDocument : class;

    Task<IReadOnlyList<TDocument>> SearchByGeoShapeAsync<TDocument>(ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class;

    Task<TDocument?> GetAsync<TDocument>(
        string indexName,
        string id)
        where TDocument : class;

    Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class;
}

public interface IElasticsearchPointInTimeClient
{
    Task<string> OpenPointInTimeAsync(
        string indexName,
        string keepAlive,
        CancellationToken cancellationToken = default);

    Task<ElasticsearchSearchPage<TDocument>> SearchPointInTimeAsync<TDocument>(
        ElasticsearchPointInTimeSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class;

    Task ClosePointInTimeAsync(
        string pointInTimeId,
        CancellationToken cancellationToken = default);
}

public sealed record ElasticsearchDocument<TDocument>(string Id, TDocument Source)
    where TDocument : class;

public sealed class ElasticsearchDocumentClient :
    IElasticsearchDocumentClient,
    IElasticsearchPointInTimeClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IElasticClient _client;

    public ElasticsearchDocumentClient(IElasticClient client)
    {
        _client = client;
    }

    public async Task<IReadOnlyList<TDocument>> SearchAsync<TDocument>(ElasticsearchSearchRequest request)
        where TDocument : class =>
        (await SearchDocumentsAsync<TDocument>(request))
            .Select(document => document.Source)
            .ToArray();

    public Task<IReadOnlyList<TDocument>> SearchBySensorAsync<TDocument>(
        ElasticsearchSensorSearchRequest request)
        where TDocument : class =>
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
        ElasticsearchGeoShapeSearchRequest request)
        where TDocument : class =>
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
        var document = await GetDocumentAsync<TDocument>(indexName, id);
        return document?.Source;
    }

    public async Task<ElasticsearchDocument<TDocument>?> GetDocumentAsync<TDocument>(
        string indexName,
        string id,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);

        var response = await _client.GetAsync<TDocument>(
            id,
            descriptor => descriptor.Index(indexName),
            cancellationToken);

        if (!response.Found && response.ApiCall?.HttpStatusCode == (int)HttpStatusCode.NotFound)
        {
            return null;
        }

        EnsureValid(response, $"get document '{id}' from index '{indexName}'");
        return response.Source is null ? null : new ElasticsearchDocument<TDocument>(response.Id, response.Source);
    }

    public async Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        var body = ElasticsearchQueryJsonBuilder.BuildSearchBody(request);
        var response = await _client.LowLevel.SearchAsync<StringResponse>(
            request.IndexName,
            PostData.String(body),
            new SearchRequestParameters(),
            cancellationToken);

        EnsureValid(response, $"search index '{request.IndexName}'");
        return DeserializeDocumentSearchResponse<TDocument>(response.Body);
    }

    public async Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(configure);

        var response = await _client.SearchAsync<TDocument>(
            descriptor => configure(descriptor),
            cancellationToken);

        EnsureValid(response, "search documents");
        return response.Hits
            .Where(hit => hit.Source is not null)
            .Select(hit => new ElasticsearchDocument<TDocument>(hit.Id, hit.Source))
            .ToArray();
    }

    public async Task<string> OpenPointInTimeAsync(
        string indexName,
        string keepAlive,
        CancellationToken cancellationToken = default)
    {
        ValidateIndex(indexName);
        ValidateKeepAlive(keepAlive);

        var parameters = new OpenPointInTimeRequestParameters
        {
            KeepAlive = keepAlive
        };

        var response = await _client.LowLevel.OpenPointInTimeAsync<StringResponse>(
            indexName,
            parameters,
            cancellationToken);

        EnsureValid(response, $"open point in time for index '{indexName}'");
        var body = DeserializeRequired<ElasticsearchOpenPointInTimeResponse>(
            response.Body,
            $"open point in time for index '{indexName}'");
        if (string.IsNullOrWhiteSpace(body.Id))
        {
            throw MalformedResponse($"open point in time for index '{indexName}'", "the id is missing");
        }

        return body.Id;
    }

    public async Task<ElasticsearchSearchPage<TDocument>> SearchPointInTimeAsync<TDocument>(
        ElasticsearchPointInTimeSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(request);

        var body = ElasticsearchQueryJsonBuilder.BuildPointInTimeSearchBody(request);
        var response = await _client.LowLevel.SearchAsync<StringResponse>(
            PostData.String(body),
            new SearchRequestParameters
            {
                AllowPartialSearchResults = false
            },
            cancellationToken);

        EnsureValid(response, "search point in time");
        return DeserializePointInTimeSearchResponse<TDocument>(
            response.Body,
            request.Search.Size,
            request.TrackTotalHits);
    }

    public async Task ClosePointInTimeAsync(
        string pointInTimeId,
        CancellationToken cancellationToken = default)
    {
        ValidatePointInTimeId(pointInTimeId);

        var body = JsonSerializer.Serialize(new { id = pointInTimeId }, JsonOptions);
        var response = await _client.LowLevel.ClosePointInTimeAsync<StringResponse>(
            PostData.String(body),
            new ClosePointInTimeRequestParameters(),
            cancellationToken);

        EnsureValid(response, "close point in time");
        var closeResponse = DeserializeRequired<ElasticsearchClosePointInTimeResponse>(
            response.Body,
            "close point in time");
        if (!closeResponse.Succeeded)
        {
            throw MalformedResponse("close point in time", "Elasticsearch reported that it did not succeed");
        }
    }

    public async Task<string> IndexAsync<TDocument>(
        string indexName,
        string? id,
        TDocument document,
        bool waitForRefresh = true,
        bool allowGeneratedId = false,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ValidateIndex(indexName);
        if (!allowGeneratedId)
        {
            ValidateId(id);
        }

        ArgumentNullException.ThrowIfNull(document);

        var response = await _client.IndexAsync(
            document,
            descriptor =>
            {
                descriptor = descriptor.Index(indexName);
                if (!string.IsNullOrWhiteSpace(id))
                {
                    descriptor = descriptor.Id(id);
                }

                return waitForRefresh ? descriptor.Refresh(Refresh.WaitFor) : descriptor;
            },
            cancellationToken);

        EnsureValid(response, $"index document '{id}' into index '{indexName}'");
        return response.Id;
    }

    public async Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);

        var response = await _client.DeleteAsync<TDocument>(
            id,
            descriptor =>
            {
                descriptor = descriptor.Index(indexName);
                return waitForRefresh ? descriptor.Refresh(Refresh.WaitFor) : descriptor;
            },
            cancellationToken);

        if (response.Result == Result.NotFound)
        {
            return false;
        }

        EnsureValid(response, $"delete document '{id}' from index '{indexName}'");
        return true;
    }

    private static IReadOnlyList<TDocument> DeserializeSearchResponse<TDocument>(string body)
        where TDocument : class
    {
        return DeserializeDocumentSearchResponse<TDocument>(body)
            .Select(document => document.Source)
            .ToArray();
    }

    private static IReadOnlyList<ElasticsearchDocument<TDocument>> DeserializeDocumentSearchResponse<TDocument>(string body)
        where TDocument : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return [];
        }

        var response = JsonSerializer.Deserialize<ElasticsearchSearchResponse<TDocument>>(body, JsonOptions);
        return response?.Hits?.Items
            .Where(hit => hit.Source is not null)
            .Select(hit =>
            {
                HydrateSourceId(hit.Source!, hit.Id);
                return new ElasticsearchDocument<TDocument>(hit.Id, hit.Source!);
            })
            .ToArray() ?? [];
    }

    private static ElasticsearchSearchPage<TDocument> DeserializePointInTimeSearchResponse<TDocument>(
        string body,
        int requestedSize,
        bool trackTotalHits)
        where TDocument : class
    {
        var response = DeserializeRequired<ElasticsearchPointInTimeSearchResponse<TDocument>>(
            body,
            "search point in time");
        if (string.IsNullOrWhiteSpace(response.PointInTimeId))
        {
            throw MalformedResponse("search point in time", "the pit_id is missing");
        }

        if (response.TimedOut is not false)
        {
            throw MalformedResponse("search point in time", "timed_out is missing or true");
        }

        if (response.Shards is null ||
            response.Shards.Total is null ||
            response.Shards.Successful is null ||
            response.Shards.Failed is null ||
            response.Shards.Total.Value <= 0 ||
            response.Shards.Successful != response.Shards.Total ||
            response.Shards.Failed != 0)
        {
            throw MalformedResponse("search point in time", "the shard summary is missing or incomplete");
        }

        if (response.Hits?.Items is null)
        {
            throw MalformedResponse("search point in time", "hits.hits is missing");
        }

        long? total = null;
        if (response.Hits.Total is not null)
        {
            if (response.Hits.Total.Value is null ||
                response.Hits.Total.Value.Value < 0 ||
                !string.Equals(response.Hits.Total.Relation, "eq", StringComparison.Ordinal))
            {
                throw MalformedResponse("search point in time", "hits.total is not an exact non-negative count");
            }

            total = response.Hits.Total.Value.Value;
        }

        if (trackTotalHits && total is null)
        {
            throw MalformedResponse("search point in time", "hits.total is missing while exact totals are requested");
        }

        if (response.Hits.Items.Count > requestedSize ||
            (total is not null && response.Hits.Items.Count > total.Value))
        {
            throw MalformedResponse("search point in time", "the page contains more hits than expected");
        }

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var hits = new ElasticsearchSearchHit<TDocument>[response.Hits.Items.Count];
        for (var index = 0; index < response.Hits.Items.Count; index++)
        {
            var hit = response.Hits.Items[index];
            if (string.IsNullOrWhiteSpace(hit.Id))
            {
                throw MalformedResponse("search point in time", "a hit has no _id");
            }

            if (!ids.Add(hit.Id))
            {
                throw MalformedResponse("search point in time", $"the page contains duplicate _id '{hit.Id}'");
            }

            if (hit.Source is null)
            {
                throw MalformedResponse("search point in time", $"hit '{hit.Id}' has a null _source");
            }

            if (hit.SortValues is null ||
                hit.SortValues.Count != 1 ||
                hit.SortValues[0].ValueKind != JsonValueKind.Number)
            {
                throw MalformedResponse(
                    "search point in time",
                    $"hit '{hit.Id}' does not have one numeric _shard_doc sort value");
            }

            HydrateSourceId(hit.Source, hit.Id);
            hits[index] = new ElasticsearchSearchHit<TDocument>(
                hit.Id,
                hit.Source,
                hit.SortValues);
        }

        return new ElasticsearchSearchPage<TDocument>(
            response.PointInTimeId,
            total,
            hits);
    }

    private static TResponse DeserializeRequired<TResponse>(string body, string operation)
        where TResponse : class
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            throw MalformedResponse(operation, "the response body is blank");
        }

        try
        {
            return JsonSerializer.Deserialize<TResponse>(body, JsonOptions) ??
                throw MalformedResponse(operation, "the response body is null");
        }
        catch (JsonException ex)
        {
            throw new ElasticsearchClientException(
                $"Elasticsearch returned a malformed response while attempting to {operation}.",
                ex);
        }
    }

    private static void HydrateSourceId<TDocument>(TDocument source, string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return;
        }

        var property = typeof(TDocument).GetProperty(
            "Id",
            BindingFlags.Instance | BindingFlags.Public);
        if (property?.CanWrite != true || property.PropertyType != typeof(string))
        {
            return;
        }

        var currentValue = (string?)property.GetValue(source);
        if (string.IsNullOrWhiteSpace(currentValue))
        {
            property.SetValue(source, id);
        }
    }

    private static void ValidateIndexAndId(string indexName, string id)
    {
        ValidateIndex(indexName);
        ValidateId(id);
    }

    private static void ValidateIndex(string indexName)
    {
        if (string.IsNullOrWhiteSpace(indexName))
        {
            throw new ArgumentException("Index name must not be empty.", nameof(indexName));
        }
    }

    private static void ValidateId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Document id must not be empty.", nameof(id));
        }
    }

    private static void ValidateKeepAlive(string keepAlive)
    {
        if (string.IsNullOrWhiteSpace(keepAlive))
        {
            throw new ArgumentException("Point-in-time keep alive must not be empty.", nameof(keepAlive));
        }
    }

    private static void ValidatePointInTimeId(string pointInTimeId)
    {
        if (string.IsNullOrWhiteSpace(pointInTimeId))
        {
            throw new ArgumentException("Point-in-time id must not be empty.", nameof(pointInTimeId));
        }
    }

    private static ElasticsearchClientException MalformedResponse(string operation, string detail) =>
        new($"Elasticsearch returned a malformed response while attempting to {operation}: {detail}.");

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

    public ElasticsearchClientException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
