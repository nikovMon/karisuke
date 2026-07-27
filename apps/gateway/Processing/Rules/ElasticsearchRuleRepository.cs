using System.Text.Json;
using System.Text.Json.Serialization;
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

    public async Task<RuleLoadResult> GetActiveRulesAsync(CancellationToken cancellationToken)
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

    private async Task<RuleLoadResult> ReadActiveRulesAsync(
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
            var rejectedSources = new List<RuleSourceRejection>();
            var ids = new HashSet<string>(StringComparer.Ordinal);
            IReadOnlyList<JsonElement> searchAfter = [];
            long? expectedTotal = null;
            long? previousShardDocument = null;
            long processedHitCount = 0;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var page = await _client.SearchPointInTimeAsync<RawRuleSource>(
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

                    var shardDocument = ReadShardDocument(hit);
                    if (previousShardDocument is not null &&
                        shardDocument <= previousShardDocument.Value)
                    {
                        throw PaginationFailure("the _shard_doc cursor did not advance");
                    }

                    previousShardDocument = shardDocument;
                    processedHitCount++;

                    if (TryDeserializeRule(hit, out var rule, out var rejection))
                    {
                        rule.Id = hit.Id;
                        rules.Add(rule);
                    }
                    else
                    {
                        rejectedSources.Add(rejection);
                    }
                }

                if (processedHitCount > expectedTotal.Value)
                {
                    throw PaginationFailure("more rules were returned than the exact hit count");
                }

                if (processedHitCount == expectedTotal.Value)
                {
                    return new RuleLoadResult(rules, rejectedSources);
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

    private static bool TryDeserializeRule(
        ElasticsearchSearchHit<RawRuleSource> hit,
        out RuleDto rule,
        out RuleSourceRejection rejection)
    {
        rule = null!;
        rejection = null!;

        if (hit.Source is null ||
            hit.Source.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            rejection = new RuleSourceRejection(
                hit.Id,
                "Its Elasticsearch source was null.");
            return false;
        }

        if (hit.Source.Value.ValueKind != JsonValueKind.Object)
        {
            rejection = new RuleSourceRejection(
                hit.Id,
                "Its Elasticsearch source was not a JSON object.");
            return false;
        }

        try
        {
            rule = hit.Source.Value.Deserialize<RuleDto>() ??
                throw new JsonException("The deserialized rule source was null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            rejection = new RuleSourceRejection(
                hit.Id,
                "Its Elasticsearch source could not be deserialized as a rule.",
                ex);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Id) &&
            !string.Equals(rule.Id, hit.Id, StringComparison.Ordinal))
        {
            rejection = new RuleSourceRejection(
                hit.Id,
                "Its Elasticsearch source contained a conflicting rule id.");
            rule = null!;
            return false;
        }

        return true;
    }

    private static long ReadShardDocument(ElasticsearchSearchHit<RawRuleSource> hit)
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

[JsonConverter(typeof(RawRuleSourceJsonConverter))]
internal sealed record RawRuleSource(JsonElement Value);

internal sealed class RawRuleSourceJsonConverter : JsonConverter<RawRuleSource>
{
    public override bool HandleNull => true;

    public override RawRuleSource Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return new RawRuleSource(document.RootElement.Clone());
    }

    public override void Write(
        Utf8JsonWriter writer,
        RawRuleSource value,
        JsonSerializerOptions options) =>
        value.Value.WriteTo(writer);
}
