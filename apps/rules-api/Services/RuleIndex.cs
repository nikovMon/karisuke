using ImagingPipeline.ElasticsearchClient;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Elasticsearch access to one rules index, shared by the v1 and the pipeline-scoped rule services.
/// Client failures surface as <see cref="RulePersistenceException"/>.
/// </summary>
internal sealed class RuleIndex(IElasticsearchDocumentClient client, string name)
{
    private const string RuleNameKeywordField = "ruleName.keyword";
    private const string IsActiveField = "isActive";
    private const string RuleNameField = "ruleName";

    public Task<IReadOnlyList<ElasticsearchDocument<TRule>>> SearchAsync<TRule>(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken)
        where TRule : class =>
        SearchAsync<TRule>(BuildSearchRequest(isActive, from, size), cancellationToken);

    public async Task<IReadOnlyList<string>> SearchNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken)
    {
        var request = BuildSearchRequest(isActive, from, size);
        request.SourceIncludes.Add(RuleNameField);

        var documents = await SearchAsync<RuleNameProjection>(request, cancellationToken);
        return documents
            .Select(document => document.Source.RuleName)
            .ToArray();
    }

    public Task<ElasticsearchDocument<TRule>?> GetAsync<TRule>(string id, CancellationToken cancellationToken)
        where TRule : class =>
        ExecuteAsync(
            () => client.GetDocumentAsync<TRule>(name, id, cancellationToken),
            $"get rule '{id}'");

    public async Task<ElasticsearchDocument<TRule>?> GetByNameAsync<TRule>(
        string ruleName,
        CancellationToken cancellationToken)
        where TRule : class
    {
        var documents = await SearchAsync<TRule>(BuildNameSearchRequest(ruleName, excludingId: null), cancellationToken);
        return documents.FirstOrDefault();
    }

    public async Task<bool> ExistsByNameAsync<TRule>(
        string ruleName,
        string? excludingId,
        CancellationToken cancellationToken)
        where TRule : class
    {
        var documents = await SearchAsync<TRule>(BuildNameSearchRequest(ruleName, excludingId), cancellationToken);
        return documents.Count > 0;
    }

    public Task<string> SaveAsync<TRule>(string? id, TRule rule, CancellationToken cancellationToken)
        where TRule : class =>
        ExecuteAsync(
            () => client.IndexAsync(
                name,
                id,
                rule,
                waitForRefresh: false,
                allowGeneratedId: true,
                cancellationToken),
            $"save rule '{id}'");

    public Task<bool> DeleteAsync<TRule>(string id, CancellationToken cancellationToken)
        where TRule : class =>
        ExecuteAsync(
            () => client.DeleteAsync<TRule>(
                name,
                id,
                waitForRefresh: false,
                cancellationToken),
            $"delete rule '{id}'");

    private Task<IReadOnlyList<ElasticsearchDocument<TRule>>> SearchAsync<TRule>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken)
        where TRule : class =>
        ExecuteAsync(
            () => client.SearchDocumentsAsync<TRule>(request, cancellationToken),
            "search rules");

    private ElasticsearchSearchRequest BuildSearchRequest(bool? isActive, int from, int size)
    {
        var request = new ElasticsearchSearchRequest
        {
            IndexName = name,
            From = from,
            Size = size
        };

        if (isActive.HasValue)
        {
            request.TermFilters.Add(new ElasticsearchTermFilter
            {
                Field = IsActiveField,
                Value = isActive.Value
            });
        }

        return request;
    }

    private ElasticsearchSearchRequest BuildNameSearchRequest(string ruleName, string? excludingId)
    {
        var request = new ElasticsearchSearchRequest
        {
            IndexName = name,
            Size = 1,
            TermFilters =
            [
                new ElasticsearchTermFilter
                {
                    Field = RuleNameKeywordField,
                    Value = ruleName
                }
            ]
        };

        if (!string.IsNullOrWhiteSpace(excludingId))
        {
            request.ExcludedIds.Add(excludingId);
        }

        return request;
    }

    private static async Task<TResult> ExecuteAsync<TResult>(
        Func<Task<TResult>> operation,
        string description)
    {
        try
        {
            return await operation();
        }
        catch (ElasticsearchClientException exception)
        {
            throw new RulePersistenceException(
                $"Elasticsearch failed to {description}: {exception.Message}",
                exception);
        }
    }

    private sealed class RuleNameProjection
    {
        [JsonPropertyName("ruleName")]
        public string RuleName { get; set; } = string.Empty;
    }
}
