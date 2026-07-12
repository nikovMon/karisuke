using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Gateway.Errors;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class ElasticsearchRuleRepository : IRuleRepository
{
    private const int PageSize = 500;

    private readonly IElasticsearchDocumentClient _client;
    private readonly string _indexName;

    public ElasticsearchRuleRepository(
        IElasticsearchDocumentClient client,
        IOptions<ElasticsearchClientOptions> settings)
    {
        _client = client;
        _indexName = settings.Value.Index;
    }

    public async Task<IReadOnlyList<RuleDto>> GetActiveRulesAsync(CancellationToken cancellationToken)
    {
        var rules = new List<RuleDto>();
        var from = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            IReadOnlyList<RuleDto> page;
            try
            {
                page = await _client.SearchAsync<RuleDto>(
                    new ElasticsearchSearchRequest
                    {
                        IndexName = _indexName,
                        From = from,
                        Size = PageSize,
                        TermFilters =
                        [
                            new ElasticsearchTermFilter
                            {
                                Field = "isActive",
                                Value = true
                            }
                        ]
                    });
            }
            catch (ElasticsearchClientException ex)
            {
                throw new GatewayDependencyException(
                    "Elasticsearch search for active rules failed.",
                    ex);
            }

            rules.AddRange(page);

            if (page.Count < PageSize)
            {
                break;
            }

            from += page.Count;
        }

        return rules;
    }
}
