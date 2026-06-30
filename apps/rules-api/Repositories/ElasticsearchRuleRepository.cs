using Elasticsearch.Net;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Common.Dtos.Rules.Models;
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
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.SearchAsync<RuleConfigDto>(descriptor =>
        {
            descriptor = descriptor.Index(_indexName).From(from).Size(size);
            return isActive.HasValue
                ? descriptor.Query(query => query.Term(rule => rule.IsActive, isActive.Value))
                : descriptor.Query(query => query.MatchAll());
        }, cancellationToken);

        EnsureValid(response, "search rules");
        return response.Hits
            .Where(hit => hit.Source is not null)
            .Select(HydrateId)
            .ToArray();
    }

    public async Task<RuleConfigDto?> GetByIdAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var response = await _client.GetAsync<RuleConfigDto>(
            id,
            descriptor => descriptor.Index(_indexName),
            cancellationToken);

        if (!response.Found && response.ApiCall?.HttpStatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }

        EnsureValid(response, $"get rule '{id}'");
        if (response.Source is null)
        {
            return null;
        }

        response.Source.Id = response.Id;
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
        var hit = response.Hits.FirstOrDefault();
        return hit?.Source is null ? null : HydrateId(hit);
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
        return response.Hits.Any(hit =>
            !string.Equals(hit.Id, excludingId, StringComparison.Ordinal));
    }

    public async Task SaveAsync(RuleConfigDto rule, CancellationToken cancellationToken = default)
    {
        var response = await _client.IndexAsync(
            rule,
            descriptor =>
            {
                descriptor = descriptor.Index(_indexName).Refresh(Refresh.WaitFor);
                return string.IsNullOrWhiteSpace(rule.Id)
                    ? descriptor
                    : descriptor.Id(rule.Id);
            },
            cancellationToken);

        EnsureValid(response, $"save rule '{rule.Id}'");
        rule.Id = response.Id;
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

    private static RuleConfigDto HydrateId(IHit<RuleConfigDto> hit)
    {
        hit.Source.Id = hit.Id;
        return hit.Source;
    }
}
