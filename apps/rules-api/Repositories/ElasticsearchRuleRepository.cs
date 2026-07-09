using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Common.Dtos.Rules.Models;
using Microsoft.Extensions.Options;
using Nest;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Api.Repositories;

public sealed class ElasticsearchRuleRepository : IRuleRepository
{
    private readonly IElasticsearchDocumentClient _client;
    private readonly string _indexName;

    public ElasticsearchRuleRepository(
        IElasticsearchDocumentClient client,
        IOptions<RulesElasticsearchOptions> options)
    {
        _client = client;
        _indexName = options.Value.IndexName;
    }

    public async Task<IReadOnlyList<RuleDto>> GetAllAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var documents = await SearchAsync<RuleDto>(descriptor =>
            BuildActiveFilter(descriptor.Index(_indexName).From(from).Size(size), isActive), cancellationToken);

        return documents
            .Select(HydrateId)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var documents = await SearchAsync<RuleNameProjection>(descriptor =>
            BuildActiveFilter(
                descriptor
                    .Index(_indexName)
                    .From(from)
                    .Size(size)
                    .Source(source => source
                        .Includes(fields => fields
                            .Field(rule => rule.RuleName))),
                isActive),
            cancellationToken);

        return documents
            .Select(document => document.Source.RuleName)
            .ToArray();
    }

    public async Task<RuleDto?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var document = await GetDocumentAsync(id, cancellationToken);
        return document is null ? null : HydrateId(document);
    }

    public async Task<RuleDto?> GetByNameAsync(
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        var documents = await SearchAsync<RuleDto>(descriptor => descriptor
            .Index(_indexName)
            .Size(1)
            .Query(query => query.Term("ruleName.keyword", ruleName)), cancellationToken);

        var document = documents.FirstOrDefault();
        return document is null ? null : HydrateId(document);
    }

    public async Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        var documents = await SearchAsync<RuleDto>(descriptor =>
        {
            descriptor = descriptor
                .Index(_indexName)
                .Size(1);

            return string.IsNullOrWhiteSpace(excludingId)
                ? descriptor.Query(query => query.Term("ruleName.keyword", ruleName))
                : descriptor.Query(query => query.Bool(boolean => boolean
                    .Must(must => must.Term("ruleName.keyword", ruleName))
                    .MustNot(mustNot => mustNot.Ids(ids => ids.Values(excludingId)))));
        }, cancellationToken);

        return documents.Count > 0;
    }

    public async Task SaveAsync(RuleDto rule, CancellationToken cancellationToken = default)
    {
        rule.Id = await ExecuteAsync(
            () => _client.IndexAsync(
                _indexName,
                rule.Id,
                rule,
                waitForRefresh: false,
                allowGeneratedId: true,
                cancellationToken),
            $"save rule '{rule.Id}'");
    }

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        ExecuteAsync(
            () => _client.DeleteAsync<RuleDto>(
                _indexName,
                id,
                waitForRefresh: false,
                cancellationToken),
            $"delete rule '{id}'");

    private Task<ElasticsearchDocument<RuleDto>?> GetDocumentAsync(
        string id,
        CancellationToken cancellationToken) =>
        ExecuteAsync(
            () => _client.GetDocumentAsync<RuleDto>(_indexName, id, cancellationToken),
            $"get rule '{id}'");

    private Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchAsync<TDocument>(
        Func<SearchDescriptor<TDocument>, ISearchRequest> configure,
        CancellationToken cancellationToken)
        where TDocument : class =>
        ExecuteAsync(
            () => _client.SearchDocumentsAsync(configure, cancellationToken),
            "search rules");

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
            throw new RuleRepositoryException($"Elasticsearch failed to {description}: {exception.Message}");
        }
    }

    private static SearchDescriptor<TDocument> BuildActiveFilter<TDocument>(
        SearchDescriptor<TDocument> descriptor,
        bool? isActive)
        where TDocument : class =>
        isActive.HasValue
            ? descriptor.Query(query => query.Term("isActive", isActive.Value))
            : descriptor.Query(query => query.MatchAll());

    private static RuleDto HydrateId(ElasticsearchDocument<RuleDto> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }

    private sealed class RuleNameProjection
    {
        [JsonPropertyName("ruleName")]
        [PropertyName("ruleName")]
        public string RuleName { get; set; } = string.Empty;
    }
}
