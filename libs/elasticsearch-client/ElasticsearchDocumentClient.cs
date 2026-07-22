using System.Net;
using System.Reflection;
using System.Text.Json;
using Elasticsearch.Net;
using ImagingPipeline.Observability;
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

public sealed record ElasticsearchDocument<TDocument>(string Id, TDocument Source)
    where TDocument : class;

public sealed class ElasticsearchDocumentClient : IElasticsearchDocumentClient
{
    private const int MaximumErrorResponseInspectionLength = 32 * 1024;
    private const int MaximumServerReasonLength = 512;
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
        using var telemetry = ElasticsearchOperationTelemetry.Start(
            DependencyOperation.Get,
            indexName,
            id,
            requestedDocumentCount: 1);

        try
        {
            var response = await _client.GetAsync<TDocument>(
                id,
                descriptor => descriptor.Index(indexName),
                cancellationToken);
            telemetry.SetResponseMetadata(response.ApiCall);

            if (!response.Found && response.ApiCall?.HttpStatusCode == (int)HttpStatusCode.NotFound)
            {
                telemetry.Complete(0);
                return null;
            }

            EnsureValid(response, $"get document '{id}' from index '{indexName}'");
            var document = response.Source is null
                ? null
                : new ElasticsearchDocument<TDocument>(response.Id, response.Source);
            telemetry.Complete(document is null ? 0 : 1);
            return document;
        }
        catch (Exception exception)
        {
            telemetry.Fail(exception, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        var body = ElasticsearchQueryJsonBuilder.BuildSearchBody(request);
        using var telemetry = ElasticsearchOperationTelemetry.Start(
            DependencyOperation.Search,
            request.IndexName,
            requestedDocumentCount: request.Size);

        try
        {
            var response = await _client.LowLevel.SearchAsync<StringResponse>(
                request.IndexName,
                PostData.String(body),
                new SearchRequestParameters(),
                cancellationToken);
            telemetry.SetResponseMetadata(response.ApiCall);

            EnsureValid(response, $"search index '{request.IndexName}'");
            var documents = DeserializeDocumentSearchResponse<TDocument>(response.Body);
            telemetry.Complete(documents.Count);
            return documents;
        }
        catch (Exception exception)
        {
            telemetry.Fail(exception, cancellationToken);
            throw;
        }
    }

    public async Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchDocumentsAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ArgumentNullException.ThrowIfNull(configure);
        using var telemetry = ElasticsearchOperationTelemetry.Start(DependencyOperation.Search);

        try
        {
            var response = await _client.SearchAsync<TDocument>(
                descriptor =>
                {
                    var configuredRequest = configure(descriptor);
                    var indexName = configuredRequest.Index is null
                        ? null
                        : ((IUrlParameter)configuredRequest.Index).GetString(_client.ConnectionSettings);
                    telemetry.SetIndex(indexName);
                    return configuredRequest;
                },
                cancellationToken);
            telemetry.SetResponseMetadata(response.ApiCall);

            EnsureValid(response, "search documents");
            var documents = response.Hits
                .Where(hit => hit.Source is not null)
                .Select(hit => new ElasticsearchDocument<TDocument>(hit.Id, hit.Source))
                .ToArray();
            telemetry.Complete(documents.Length);
            return documents;
        }
        catch (Exception exception)
        {
            telemetry.Fail(exception, cancellationToken);
            throw;
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
        using var telemetry = ElasticsearchOperationTelemetry.Start(
            DependencyOperation.Index,
            indexName,
            id,
            requestedDocumentCount: 1);

        try
        {
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
            telemetry.SetResponseMetadata(response.ApiCall);

            EnsureValid(response, $"index document '{id}' into index '{indexName}'");
            telemetry.SetDocumentId(response.Id);
            telemetry.Complete(1);
            return response.Id;
        }
        catch (Exception exception)
        {
            telemetry.Fail(exception, cancellationToken);
            throw;
        }
    }

    public async Task<bool> DeleteAsync<TDocument>(
        string indexName,
        string id,
        bool waitForRefresh = true,
        CancellationToken cancellationToken = default)
        where TDocument : class
    {
        ValidateIndexAndId(indexName, id);
        using var telemetry = ElasticsearchOperationTelemetry.Start(
            DependencyOperation.Delete,
            indexName,
            id,
            requestedDocumentCount: 1);

        try
        {
            var response = await _client.DeleteAsync<TDocument>(
                id,
                descriptor =>
                {
                    descriptor = descriptor.Index(indexName);
                    return waitForRefresh ? descriptor.Refresh(Refresh.WaitFor) : descriptor;
                },
                cancellationToken);
            telemetry.SetResponseMetadata(response.ApiCall);

            if (response.Result == Result.NotFound)
            {
                telemetry.Complete(0);
                return false;
            }

            EnsureValid(response, $"delete document '{id}' from index '{indexName}'");
            telemetry.Complete(1);
            return true;
        }
        catch (Exception exception)
        {
            telemetry.Fail(exception, cancellationToken);
            throw;
        }
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

    private static void EnsureValid(IResponse response, string operation)
    {
        if (response.IsValid)
        {
            return;
        }

        var reason = response.ServerError?.Error?.Reason ?? response.OriginalException?.Message;
        throw new ElasticsearchClientException(
            BuildFailureMessage(operation, response.ApiCall?.HttpStatusCode, reason),
            response.OriginalException);
    }

    private static void EnsureValid(StringResponse response, string operation)
    {
        if (response.Success && response.HttpStatusCode is >= 200 and < 300)
        {
            return;
        }

        var reason = response.OriginalException?.Message ?? TryReadServerReason(response.Body);
        throw new ElasticsearchClientException(
            BuildFailureMessage(operation, response.HttpStatusCode, reason),
            response.OriginalException);
    }

    private static string BuildFailureMessage(string operation, int? statusCode, string? serverReason)
    {
        var message = $"Elasticsearch failed to {operation}";
        if (statusCode.HasValue)
        {
            message += $" with status {statusCode.Value}";
        }

        var safeReason = TruncateServerReason(serverReason);
        return string.IsNullOrEmpty(safeReason)
            ? $"{message}."
            : $"{message}: {safeReason}";
    }

    private static string? TryReadServerReason(string? responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody) ||
            responseBody.Length > MaximumErrorResponseInspectionLength)
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (!document.RootElement.TryGetProperty("error", out var error))
            {
                return null;
            }

            if (error.ValueKind == JsonValueKind.String)
            {
                return error.GetString();
            }

            return error.ValueKind == JsonValueKind.Object &&
                error.TryGetProperty("reason", out var reason) &&
                reason.ValueKind == JsonValueKind.String
                    ? reason.GetString()
                    : null;
        }
        catch (JsonException)
        {
            // Raw or malformed response bodies are intentionally excluded from exceptions.
            return null;
        }
    }

    private static string? TruncateServerReason(string? serverReason)
    {
        if (string.IsNullOrWhiteSpace(serverReason))
        {
            return null;
        }

        var normalized = serverReason
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        return normalized.Length <= MaximumServerReasonLength
            ? normalized
            : normalized[..MaximumServerReasonLength];
    }
}

public sealed class ElasticsearchClientException : Exception
{
    public ElasticsearchClientException(string message)
        : base(message)
    {
    }

    public ElasticsearchClientException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
