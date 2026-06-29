using Elastic.Clients.Elasticsearch;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Domain;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Application.Rules;

public sealed class ElasticsearchRuleRepository : IRuleRepository
{
    private const int PageSize = 500;

    private readonly ElasticsearchClient _client;
    private readonly RuleValidator _validator;
    private readonly string _indexName;

    public ElasticsearchRuleRepository(
        ElasticsearchClient client,
        IOptions<ElasticsearchSettings> settings,
        RuleValidator validator)
    {
        _client = client;
        _validator = validator;
        _indexName = settings.Value.IndexName;
    }

    public async Task<IReadOnlyList<RuleConfigDto>> GetActiveRulesAsync(CancellationToken cancellationToken)
    {
        var rules = new List<RuleConfigDto>();
        var from = 0;

        while (true)
        {
            var response = await _client.SearchAsync<RuleConfigDto>(
                _indexName,
                descriptor => descriptor
                    .From(from)
                    .Size(PageSize)
                    .Query(query => query.Term(term => term.Field("isActive").Value(true))),
                cancellationToken);

            if (!response.IsValidResponse)
            {
                throw new InfrastructureUnavailableException(
                    $"Elasticsearch search for active rules failed: {response.DebugInformation}");
            }

            var hits = response.HitsMetadata?.Hits ?? [];
            foreach (var hit in hits)
            {
                if (hit.Source is null)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(hit.Source.Id) && hit.Id is not null)
                {
                    hit.Source.Id = hit.Id.ToString();
                }

                rules.Add(hit.Source);
            }

            if (hits.Count < PageSize)
            {
                break;
            }

            from += hits.Count;
        }

        _validator.ValidateSnapshot(rules);
        return rules;
    }
}
