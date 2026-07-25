using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Gateway.Errors;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class ElasticsearchRuleRepository : IRuleRepository
{
    private const int PageSize = 500;
    private const string PointInTimeKeepAlive = "1m";

    private readonly IElasticsearchPointInTimeClient _client;
    private readonly string _indexName;
    private readonly ILogger<ElasticsearchRuleRepository> _logger;

    public ElasticsearchRuleRepository(
        IElasticsearchPointInTimeClient client,
        IOptions<ElasticsearchClientOptions> settings,
        ILogger<ElasticsearchRuleRepository> logger)
    {
        _client = client;
        _indexName = settings.Value.Index;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RuleDto>> GetActiveRulesAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await ReadActiveRulesAsync(cancellationToken);
        }
        catch (ElasticsearchClientException ex)
        {
            throw new GatewayDependencyException(
                "Elasticsearch search for active rules failed.",
                ex);
        }
    }

    private async Task<IReadOnlyList<RuleDto>> ReadActiveRulesAsync(
        CancellationToken cancellationToken)
    {
        string? pointInTimeId = null;

        try
        {
            pointInTimeId = await _client.OpenPointInTimeAsync(
                _indexName,
                PointInTimeKeepAlive,
                cancellationToken);

            var rules = new List<RuleDto>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<JsonElement> searchAfter = [];
            long? expectedTotal = null;
            long? previousShardDocument = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await _client.SearchPointInTimeAsync<RuleDto>(
                    new ElasticsearchPointInTimeSearchRequest
                    {
                        Search = ActiveRulesSearch(),
                        PointInTimeId = pointInTimeId,
                        KeepAlive = PointInTimeKeepAlive,
                        SearchAfter = searchAfter,
                        TrackTotalHits = expectedTotal is null
                    },
                    cancellationToken);

                if (string.IsNullOrWhiteSpace(page.PointInTimeId))
                {
                    throw PaginationFailure("a search page did not contain a PIT id");
                }

                pointInTimeId = page.PointInTimeId;
                ValidateTotal(page.Total, expectedTotal);
                if (expectedTotal is null)
                {
                    expectedTotal = page.Total!.Value;
                    if (expectedTotal > int.MaxValue)
                    {
                        throw PaginationFailure("the exact hit count exceeds the supported collection size");
                    }

                    rules.Capacity = (int)expectedTotal.Value;
                }

                if (page.Hits.Count > PageSize)
                {
                    throw PaginationFailure("a search page exceeded the requested page size");
                }

                foreach (var hit in page.Hits)
                {
                    if (string.IsNullOrWhiteSpace(hit.Id))
                    {
                        throw PaginationFailure("a search hit did not contain an id");
                    }

                    if (!ids.Add(hit.Id))
                    {
                        throw PaginationFailure($"duplicate rule id '{hit.Id}' was returned");
                    }

                    if (hit.Source is null)
                    {
                        throw PaginationFailure($"rule '{hit.Id}' had a null source");
                    }

                    if (!string.IsNullOrWhiteSpace(hit.Source.Id) &&
                        !string.Equals(hit.Source.Id, hit.Id, StringComparison.Ordinal))
                    {
                        throw PaginationFailure($"rule '{hit.Id}' had a conflicting source id");
                    }

                    var shardDocument = ReadShardDocument(hit);
                    if (previousShardDocument is not null &&
                        shardDocument <= previousShardDocument.Value)
                    {
                        throw PaginationFailure("the _shard_doc cursor did not advance");
                    }

                    previousShardDocument = shardDocument;
                    hit.Source.Id = hit.Id;
                    rules.Add(hit.Source);
                }

                if (rules.Count > expectedTotal.Value)
                {
                    throw PaginationFailure("more rules were returned than the exact hit count");
                }

                if (rules.Count == expectedTotal.Value)
                {
                    return rules;
                }

                if (page.Hits.Count == 0 || page.Hits.Count < PageSize)
                {
                    throw PaginationFailure("pagination ended before the exact hit count was reached");
                }

                searchAfter = page.Hits[^1].SortValues
                    .Select(value => value.Clone())
                    .ToArray();
            }
        }
        finally
        {
            if (pointInTimeId is not null)
            {
                try
                {
                    await _client.ClosePointInTimeAsync(
                        pointInTimeId,
                        CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(
                        ex,
                        "Failed to close Elasticsearch point in time {PointInTimeId}; it will expire after {PointInTimeKeepAlive}.",
                        pointInTimeId,
                        PointInTimeKeepAlive);
                }
            }
        }
    }

    private ElasticsearchSearchRequest ActiveRulesSearch() =>
        new()
        {
            IndexName = _indexName,
            Size = PageSize,
            TermFilters =
            [
                new ElasticsearchTermFilter
                {
                    Field = "isActive",
                    Value = true
                }
            ]
        };

    private static void ValidateTotal(long? total, long? expectedTotal)
    {
        if (total is < 0)
        {
            throw PaginationFailure("a search page contained a negative hit count");
        }

        if (expectedTotal is null && total is null)
        {
            throw PaginationFailure("the first search page did not contain an exact hit count");
        }

        if (expectedTotal is not null &&
            total is not null &&
            total.Value != expectedTotal.Value)
        {
            throw PaginationFailure("the exact hit count changed between PIT pages");
        }
    }

    private static long ReadShardDocument(ElasticsearchSearchHit<RuleDto> hit)
    {
        if (hit.SortValues is null ||
            hit.SortValues.Count != 1 ||
            !hit.SortValues[0].TryGetInt64(out var shardDocument))
        {
            throw PaginationFailure(
                $"rule '{hit.Id}' did not contain one integer _shard_doc sort value");
        }

        return shardDocument;
    }

    private static ElasticsearchClientException PaginationFailure(string detail) =>
        new($"Elasticsearch returned incomplete active-rule pagination: {detail}.");
}
