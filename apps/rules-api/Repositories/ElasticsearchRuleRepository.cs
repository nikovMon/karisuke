using Elasticsearch.Net;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Contracts.Models;
using Microsoft.Extensions.Options;
using Nest;

namespace ImagingPipeline.Rules.Api.Repositories;

public sealed class ElasticsearchRuleRepository : IRuleRepository
{
    private readonly IElasticClient _client;
    private readonly string _indexName;

    public ElasticsearchRuleRepository(
        IElasticClient client,
        IOptions<RulesElasticsearchOptions> options)
    {
        _client = client;
        _indexName = options.Value.IndexName;
    }

    public async Task<IReadOnlyList<RuleConfigDto>> GetAllAsync(
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.SearchAsync<RuleConfigDto>(descriptor =>
        {
            descriptor = descriptor.Index(_indexName).Size(10_000);
            return isActive.HasValue
                ? descriptor.Query(query => query.Term(rule => rule.IsActive, isActive.Value))
                : descriptor.Query(query => query.MatchAll());
        }, cancellationToken);

        EnsureValid(response, "search rules");
        return response.Documents.ToArray();
    }

    public async Task<RuleConfigDto?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetAsync<RuleConfigDto>(
            id,
            descriptor => descriptor.Index(_indexName),
            cancellationToken);

        if (!response.Found)
        {
            return null;
        }

        EnsureValid(response, $"get rule '{id}'");
        return response.Source;
    }

    public async Task<RuleConfigDto?> GetByNameAsync(
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.SearchAsync<RuleConfigDto>(descriptor => descriptor
            .Index(_indexName)
            .Size(1)
            .Query(query => query.Term("ruleName.keyword", ruleName)), cancellationToken);

        EnsureValid(response, $"get rule by name '{ruleName}'");
        return response.Documents.FirstOrDefault();
    }

    public async Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.SearchAsync<RuleConfigDto>(descriptor => descriptor
            .Index(_indexName)
            .Size(2)
            .Query(query => query.Term("ruleName.keyword", ruleName)), cancellationToken);

        EnsureValid(response, $"check duplicate ruleName '{ruleName}'");
        return response.Documents.Any(rule => !string.Equals(rule.Id, excludingId, StringComparison.Ordinal));
    }

    public async Task SaveAsync(RuleConfigDto rule, CancellationToken cancellationToken = default)
    {
        var response = await _client.IndexAsync(
            rule,
            descriptor => descriptor.Index(_indexName).Id(rule.Id).Refresh(Refresh.WaitFor),
            cancellationToken);

        EnsureValid(response, $"save rule '{rule.Id}'");
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        var response = await _client.DeleteAsync<RuleConfigDto>(
            id,
            descriptor => descriptor.Index(_indexName).Refresh(Refresh.WaitFor),
            cancellationToken);

        if (response.Result == Result.NotFound)
        {
            return false;
        }

        EnsureValid(response, $"delete rule '{id}'");
        return true;
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
        throw new RuleRepositoryException($"Elasticsearch failed to {operation}: {reason}");
    }
}
