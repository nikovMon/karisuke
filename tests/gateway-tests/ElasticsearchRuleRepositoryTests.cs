using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Processing.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Tests;

public sealed class ElasticsearchRuleRepositoryTests
{
    [Fact]
    public async Task GetActiveRulesAsyncUsesPitSearchAfterAndClosesLatestPit()
    {
        var firstHits = Enumerable.Range(0, 500)
            .Select(index => Hit($"rule-{index}", index))
            .ToArray();
        var client = new StubPointInTimeClient(
            Page("pit-2", 501, firstHits),
            Page("pit-3", null, Hit("rule-500", 500)));
        var repository = CreateRepository(client);

        var load = await repository.GetActiveRulesAsync(CancellationToken.None);
        var rules = load.Rules;

        Assert.Equal(501, rules.Count);
        Assert.Empty(load.RejectedSources);
        Assert.Equal(501, rules.Select(rule => rule.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(2, client.SearchRequests.Count);
        Assert.Empty(client.SearchRequests[0].SearchAfter);
        Assert.Equal(499, client.SearchRequests[1].SearchAfter[0].GetInt64());
        Assert.Equal("pit-1", client.SearchRequests[0].PointInTimeId);
        Assert.Equal("pit-2", client.SearchRequests[1].PointInTimeId);
        Assert.True(client.SearchRequests[0].TrackTotalHits);
        Assert.False(client.SearchRequests[1].TrackTotalHits);
        Assert.All(client.SearchRequests, request =>
        {
            Assert.Equal(500, request.Search.Size);
            var filter = Assert.Single(request.Search.TermFilters);
            Assert.Equal("isActive", filter.Field);
            Assert.Equal(true, filter.Value);
        });
        Assert.Equal(["pit-3"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncRejectsDuplicateIdsAcrossPagesAndStillClosesLatestPit()
    {
        var firstHits = Enumerable.Range(0, 500)
            .Select(index => Hit($"rule-{index}", index))
            .ToArray();
        var client = new StubPointInTimeClient(
            Page("pit-2", 501, firstHits),
            Page("pit-3", 501, Hit("rule-499", 500)));
        var repository = CreateRepository(client);

        var exception = await Assert.ThrowsAsync<GatewayDependencyException>(() =>
            repository.GetActiveRulesAsync(CancellationToken.None));

        Assert.Contains("duplicate rule id", exception.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(["pit-3"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncRejectsIncompletePageBeforeExactTotal()
    {
        var client = new StubPointInTimeClient(
            Page("pit-2", 2, Hit("rule-1", 1)));
        var repository = CreateRepository(client);

        var exception = await Assert.ThrowsAsync<GatewayDependencyException>(() =>
            repository.GetActiveRulesAsync(CancellationToken.None));

        Assert.Contains("before the exact hit count", exception.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(["pit-2"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncRejectsEmptyPageBeforeExactTotal()
    {
        var client = new StubPointInTimeClient(Page("pit-2", 1));
        var repository = CreateRepository(client);

        var exception = await Assert.ThrowsAsync<GatewayDependencyException>(() =>
            repository.GetActiveRulesAsync(CancellationToken.None));

        Assert.Contains("before the exact hit count", exception.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(["pit-2"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncRejectsTotalChangesBetweenPages()
    {
        var firstHits = Enumerable.Range(0, 500)
            .Select(index => Hit($"rule-{index}", index))
            .ToArray();
        var client = new StubPointInTimeClient(
            Page("pit-2", 501, firstHits),
            Page("pit-3", 500));
        var repository = CreateRepository(client);

        var exception = await Assert.ThrowsAsync<GatewayDependencyException>(() =>
            repository.GetActiveRulesAsync(CancellationToken.None));

        Assert.Contains("changed between PIT pages", exception.InnerException?.Message, StringComparison.Ordinal);
        Assert.Equal(["pit-3"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncClosesPitWhenSearchFails()
    {
        var logger = new RecordingLogger<ElasticsearchRuleRepository>();
        var client = new StubPointInTimeClient
        {
            SearchFailure = new ElasticsearchClientException("search failed"),
            CloseFailure = new ElasticsearchClientException("close failed")
        };
        var repository = CreateRepository(client, logger);

        var exception = await Assert.ThrowsAsync<GatewayDependencyException>(() =>
            repository.GetActiveRulesAsync(CancellationToken.None));

        Assert.IsType<ElasticsearchClientException>(exception.InnerException);
        Assert.Equal("search failed", exception.InnerException.Message);
        Assert.Equal(["pit-1"], client.ClosedPointInTimeIds);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(client.CloseFailure, warning.Exception);
    }

    [Fact]
    public async Task GetActiveRulesAsyncReturnsValidatedSnapshotAndWarnsWhenPitCannotBeClosed()
    {
        var logger = new RecordingLogger<ElasticsearchRuleRepository>();
        var client = new StubPointInTimeClient(Page("pit-2", 0))
        {
            CloseFailure = new ElasticsearchClientException("close failed")
        };
        var repository = CreateRepository(client, logger);

        var load = await repository.GetActiveRulesAsync(CancellationToken.None);

        Assert.Empty(load.Rules);
        Assert.Empty(load.RejectedSources);
        Assert.Equal(["pit-2"], client.ClosedPointInTimeIds);
        var warning = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Same(client.CloseFailure, warning.Exception);
        Assert.Contains("pit-2", warning.Message, StringComparison.Ordinal);
    }

    private static ElasticsearchRuleRepository CreateRepository(
        IElasticsearchPointInTimeClient client,
        ILogger<ElasticsearchRuleRepository>? logger = null) =>
        new(
            client,
            Options.Create(new ElasticsearchClientOptions
            {
                Index = "rules"
            }),
            logger ?? NullLogger<ElasticsearchRuleRepository>.Instance);

    [Fact]
    public async Task GetActiveRulesAsyncIsolatesInvalidSourceAfterCompletingPitScan()
    {
        var client = new StubPointInTimeClient(
            Page(
                "pit-2",
                2,
                RawHit(
                    "invalid-rule",
                    1,
                    """
                    {
                      "ruleName": "invalid",
                      "algorithmName": ["Unknown"]
                    }
                    """),
                Hit("valid-rule", 2)));
        var repository = CreateRepository(client);

        var load = await repository.GetActiveRulesAsync(CancellationToken.None);

        Assert.Equal(2, load.SourceRuleCount);
        Assert.Equal("valid-rule", Assert.Single(load.Rules).Id);
        var rejection = Assert.Single(load.RejectedSources);
        Assert.Equal("invalid-rule", rejection.RuleId);
        Assert.IsType<JsonException>(rejection.Exception);
        Assert.Equal(["pit-2"], client.ClosedPointInTimeIds);
    }

    [Fact]
    public async Task GetActiveRulesAsyncTreatsNonObjectSourceAsAnInvalidRule()
    {
        var client = new StubPointInTimeClient(
            Page("pit-2", 1, RawHit("invalid-rule", 1, "null")));
        var repository = CreateRepository(client);

        var load = await repository.GetActiveRulesAsync(CancellationToken.None);

        Assert.Empty(load.Rules);
        Assert.Equal("invalid-rule", Assert.Single(load.RejectedSources).RuleId);
        Assert.Equal(1, load.SourceRuleCount);
        Assert.Equal(["pit-2"], client.ClosedPointInTimeIds);
    }

    private static ElasticsearchSearchPage<RawRuleSource> Page(
        string pointInTimeId,
        long? total,
        params ElasticsearchSearchHit<RawRuleSource>[] hits) =>
        new(pointInTimeId, total, hits);

    private static ElasticsearchSearchHit<RawRuleSource> Hit(
        string id,
        long shardDocument) =>
        new(
            id,
            new RawRuleSource(JsonSerializer.SerializeToElement(new RuleDto
            {
                Id = id,
                RuleName = id,
                AlgorithmNames = [AlgorithmName.FindAir]
            })),
            [JsonSerializer.SerializeToElement(shardDocument)]);

    private static ElasticsearchSearchHit<RawRuleSource> RawHit(
        string id,
        long shardDocument,
        string sourceJson) =>
        new(
            id,
            new RawRuleSource(JsonSerializer.Deserialize<JsonElement>(sourceJson)),
            [JsonSerializer.SerializeToElement(shardDocument)]);

    private sealed class StubPointInTimeClient : IElasticsearchPointInTimeClient
    {
        private readonly Queue<ElasticsearchSearchPage<RawRuleSource>> _pages;

        public StubPointInTimeClient(params ElasticsearchSearchPage<RawRuleSource>[] pages)
        {
            _pages = new Queue<ElasticsearchSearchPage<RawRuleSource>>(pages);
        }

        public List<ElasticsearchPointInTimeSearchRequest> SearchRequests { get; } = [];
        public List<string> ClosedPointInTimeIds { get; } = [];
        public ElasticsearchClientException? SearchFailure { get; init; }
        public ElasticsearchClientException? CloseFailure { get; init; }

        public Task<string> OpenPointInTimeAsync(
            string indexName,
            string keepAlive,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal("rules", indexName);
            Assert.Equal("1m", keepAlive);
            return Task.FromResult("pit-1");
        }

        public Task<ElasticsearchSearchPage<TDocument>> SearchPointInTimeAsync<TDocument>(
            ElasticsearchPointInTimeSearchRequest request,
            CancellationToken cancellationToken = default)
            where TDocument : class
        {
            SearchRequests.Add(request);
            if (SearchFailure is not null)
            {
                throw SearchFailure;
            }

            var page = _pages.Dequeue();
            return Task.FromResult((ElasticsearchSearchPage<TDocument>)(object)page);
        }

        public Task ClosePointInTimeAsync(
            string pointInTimeId,
            CancellationToken cancellationToken = default)
        {
            ClosedPointInTimeIds.Add(pointInTimeId);
            if (CloseFailure is not null)
            {
                throw CloseFailure;
            }

            return Task.CompletedTask;
        }
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public List<LogEntry> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Add(new LogEntry(logLevel, formatter(state, exception), exception));
    }

    private sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);
}
