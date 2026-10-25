using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Searches source fields because existing pipeline indexes can disable indexing of match.
/// Only a page of projected source and the requested result page are kept in memory.
/// </summary>
internal sealed class PipelineRuleSearch(IElasticsearchDocumentClient documents, ILogger logger)
{
    private const int PageSize = 500;
    private const int MaximumScanned = 100_000;
    private const string KeepAlive = "1m";

    public async Task<IReadOnlyList<PipelineRuleDocument>> SearchAsync(
        string indexName, bool? isActive, int from, int size, string search,
        bool namesOnly, CancellationToken cancellationToken)
    {
        if (documents is not IElasticsearchPointInTimeClient client)
        {
            throw new RulePersistenceException("Rule search requires an Elasticsearch point-in-time client.");
        }

        string? pit = null;
        try
        {
            pit = await client.OpenPointInTimeAsync(indexName, KeepAlive, cancellationToken);
            var selected = new List<PipelineRuleDocument>(size);
            IReadOnlyList<JsonElement> after = [];
            long? total = null;
            long? previousCursor = null;
            var scanned = 0;
            long matched = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = new ElasticsearchSearchRequest
                {
                    IndexName = indexName,
                    Size = Math.Min(PageSize, MaximumScanned - scanned),
                    SourceIncludes = ["ruleName", "description", "match.sensors.name", "match.sensorTypes"]
                };
                if (isActive.HasValue)
                {
                    request.TermFilters.Add(new ElasticsearchTermFilter { Field = "isActive", Value = isActive.Value });
                }

                var page = await client.SearchPointInTimeAsync<PipelineRuleDocument>(new()
                {
                    Search = request, PointInTimeId = pit, KeepAlive = KeepAlive,
                    SearchAfter = after, TrackTotalHits = total is null
                }, cancellationToken);
                if (!string.IsNullOrWhiteSpace(page.PointInTimeId)) pit = page.PointInTimeId;
                if (string.IsNullOrWhiteSpace(page.PointInTimeId) || page.Total is < 0 ||
                    total is null && page.Total is null ||
                    total is not null && page.Total is not null && total != page.Total ||
                    page.Hits.Count > request.Size)
                {
                    throw IncompleteSearch();
                }
                total ??= page.Total;

                foreach (var hit in page.Hits)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (string.IsNullOrWhiteSpace(hit.Id) || hit.SortValues.Count != 1 ||
                        !hit.SortValues[0].TryGetInt64(out var cursor) ||
                        previousCursor is not null && cursor <= previousCursor)
                    {
                        throw IncompleteSearch();
                    }
                    previousCursor = cursor;
                    scanned++;
                    if (scanned > total) throw IncompleteSearch();
                    hit.Source.Id = hit.Id;
                    if (!Matches(hit.Source, search)) continue;
                    if (matched++ < from) continue;
                    selected.Add(hit.Source);
                    if (selected.Count == size) break;
                }

                if (selected.Count == size || scanned == total) break;
                if (page.Hits.Count < request.Size) throw IncompleteSearch();
                if (scanned >= MaximumScanned)
                {
                    throw new RuleSearchLimitException(
                        $"Rule search reached its {MaximumScanned:N0}-rule scan limit. Narrow the search or filter by activity.");
                }
                after = page.Hits[^1].SortValues.Select(value => value.Clone()).ToArray();
            }

            if (selected.Count == 0 || namesOnly) return selected;

            // Fetch full sources from the same snapshot, so a concurrent edit cannot replace
            // the matching rule with a different definition between scan and hydration.
            var fullPage = await client.SearchPointInTimeAsync<PipelineRuleDocument>(new()
            {
                PointInTimeId = pit, KeepAlive = KeepAlive,
                Search = new ElasticsearchSearchRequest
                {
                    IndexName = indexName, Size = selected.Count,
                    TermsFilters = [new ElasticsearchTermsFilter
                    {
                        Field = "_id", Values = selected.Select(rule => (object)rule.Id).ToArray()
                    }]
                }
            }, cancellationToken);
            if (!string.IsNullOrWhiteSpace(fullPage.PointInTimeId)) pit = fullPage.PointInTimeId;
            var selectedIds = selected.Select(rule => rule.Id).ToHashSet(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(fullPage.PointInTimeId) || fullPage.Total != selected.Count ||
                fullPage.Hits.Count != selected.Count ||
                fullPage.Hits.Any(hit => !selectedIds.Remove(hit.Id)))
            {
                throw IncompleteSearch();
            }
            return fullPage.Hits.Select(hit =>
            {
                hit.Source.Id = hit.Id;
                return hit.Source;
            }).ToArray();
        }
        catch (ElasticsearchClientException exception)
        {
            throw new RulePersistenceException("Elasticsearch failed to search rules.", exception);
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(pit))
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await client.ClosePointInTimeAsync(pit, cleanup.Token);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Could not close the rule search point in time; it will expire.");
                }
            }
        }
    }

    private static bool Matches(PipelineRuleDocument rule, string search) =>
        Contains(rule.Id, search) || Contains(rule.RuleName, search) || Contains(rule.Description, search) ||
        rule.Match?.Sensors?.Any(sensor => Contains(sensor?.Name, search)) == true ||
        rule.Match?.SensorTypes?.Any(type => Contains(type, search)) == true;

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static RulePersistenceException IncompleteSearch() =>
        new("Elasticsearch returned an incomplete or invalid rule search snapshot. Retry the search.");
}

internal sealed class RuleSearchLimitException(string message) : Exception(message);
