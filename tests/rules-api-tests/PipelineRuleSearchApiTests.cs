using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Services;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.DependencyInjection;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelineRuleSearchApiTests
{
    [Theory]
    [InlineData("ALPHA", "id-one")]
    [InlineData("D-ONE", "id-one")]
    [InlineData("thermal", "id-two")]
    [InlineData("sar", "id-three")]
    [InlineData("city", "id-four")]
    public async Task SearchMatchesLiteralSourceFieldsAndHydratesOnlyTheSelectedPage(string search, string expectedId)
    {
        var store = new SearchStore(
            ("id-one", Rule("Alpha")),
            ("id-two", Rule("Beta", sensor: "THERMAL")),
            ("id-three", Rule("Gamma", sensorType: "SAR")),
            ("id-four", Rule("Delta", description: "City coverage")));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<PipelineRuleDocument>>(
            $"/pipelines/FindAir/rules?search={Uri.EscapeDataString(search)}");

        var rule = Assert.Single(rules!);
        Assert.Equal(expectedId, rule.Id);
        Assert.NotEmpty(rule.RunParams!);
        Assert.Equal("asd-integ-pipeline-index", Assert.Single(store.OpenedIndexes));
        Assert.Equal(2, store.Requests.Count);
        Assert.Equal(["ruleName", "description", "match.sensors.name", "match.sensorTypes"], store.Requests[0].Search.SourceIncludes);
        Assert.Empty(store.Requests[1].Search.SourceIncludes);
        var selected = Assert.Single(store.Requests[1].Search.TermsFilters);
        Assert.Equal("_id", selected.Field);
        Assert.Equal([expectedId], selected.Values);
        Assert.Equal("pit-2", store.Requests[1].PointInTimeId);
        Assert.Equal(["pit-3"], store.ClosedPits);
        using var scanBody = JsonDocument.Parse(ElasticsearchQueryJsonBuilder.BuildPointInTimeSearchBody(store.Requests[0]));
        Assert.True(scanBody.RootElement.GetProperty("query").TryGetProperty("match_all", out _));
        Assert.Equal("_shard_doc", scanBody.RootElement.GetProperty("sort")[0].GetString());
        using var fullBody = JsonDocument.Parse(ElasticsearchQueryJsonBuilder.BuildPointInTimeSearchBody(store.Requests[1]));
        Assert.Equal(expectedId, fullBody.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter")[0].GetProperty("terms").GetProperty("_id")[0].GetString());
    }

    [Fact]
    public async Task SearchScansBeyondTheFirstSourcePageAndPagesMatchingResults()
    {
        var store = new SearchStore(Enumerable.Range(0, 503)
            .Select(i => ($"rule-{i}", Rule(i >= 499 ? "needle" : "haystack"))).ToArray());
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<PipelineRuleDocument>>(
            "/pipelines/FindAir/rules?search=needle&from=2&size=1");

        Assert.Equal("rule-501", Assert.Single(rules!).Id);
        Assert.Equal(3, store.Requests.Count);
        Assert.Equal(499, store.Requests[1].SearchAfter.Single().GetInt32());
        Assert.False(store.Requests[1].TrackTotalHits);
        Assert.Equal(["pit-4"], store.ClosedPits);
    }

    [Fact]
    public async Task NamesOnlySearchHonorsActivityAndDoesNotFetchFullSources()
    {
        var store = new SearchStore(("one", Rule("Same active")), ("two", Rule("Same inactive", active: false)));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var names = await client.GetFromJsonAsync<List<string>>(
            "/pipelines/FindAir/rules?search=%20SAME%20&isActive=false&getNameOnly=true");

        Assert.Equal(["Same inactive"], names);
        var request = Assert.Single(store.Requests);
        var filter = Assert.Single(request.Search.TermFilters);
        Assert.Equal("isActive", filter.Field);
        Assert.Equal(false, filter.Value);
        Assert.Equal(["pit-2"], store.ClosedPits);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("?")]
    [InlineData("name:value OR *")]
    public async Task SearchDoesNotInterpretInputAsQuerySyntax(string search)
    {
        var store = new SearchStore(("one", Rule(search)), ("two", Rule("unrelated")));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<PipelineRuleDocument>>(
            $"/pipelines/FindAir/rules?search={Uri.EscapeDataString(search)}");

        Assert.Equal("one", Assert.Single(rules!).Id);
    }

    [Fact]
    public async Task SearchRejectsTooLongInputBeforeReachingElasticsearch()
    {
        var store = new SearchStore();
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/FindAir/rules?search=" + new string('a', 201));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(store.OpenedIndexes);
    }

    [Fact]
    public async Task BlankSearchUsesTheExistingListWithoutAPointInTime()
    {
        var store = new SearchStore(("one", Rule("any")));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<PipelineRuleDocument>>("/pipelines/FindAir/rules?search=%20%20");

        Assert.Equal("one", Assert.Single(rules!).Id);
        Assert.Empty(store.OpenedIndexes);
    }

    [Fact]
    public async Task SearchLimitIsExplicitAndClosesTheSnapshot()
    {
        var store = new SearchStore { SyntheticCount = 100_001 };
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/FindAir/rules?search=missing");

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Contains("scan limit", await response.Content.ReadAsStringAsync());
        Assert.Equal(200, store.Requests.Count);
        Assert.Equal(["pit-201"], store.ClosedPits);
    }

    [Fact]
    public async Task SearchClosesTheLatestSnapshotAfterDependencyFailure()
    {
        var store = new SearchStore { SyntheticCount = 501, FailRead = 2 };
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/FindAir/rules?search=missing");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal(["pit-2"], store.ClosedPits);
    }

    [Fact]
    public async Task SearchClosesTheSnapshotWhenTheCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new SearchStore { BeforeRead = cancellation.Cancel };
        using var factory = new RulesApiFactory(store);
        var service = factory.Services.GetRequiredService<PipelineRuleService>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.GetRulesAsync(
            "FindAir", null, 0, 10, cancellation.Token, "missing"));

        Assert.Equal(["pit-1"], store.ClosedPits);
        Assert.False(store.CloseTokenWasCancelled);
    }

    private static PipelineRuleDocument Rule(string name, string? sensor = null, string? sensorType = null,
        string? description = null, bool active = true) => new()
    {
        RuleName = name, Description = description, IsActive = active,
        Match = new RuleMatchConditions
        {
            Sensors = sensor is null ? null : [new SensorConfig { Name = sensor }],
            SensorTypes = sensorType is null ? null : [sensorType]
        },
        RunParams = [JsonSerializer.SerializeToElement(new { tenantId = "tenant" })]
    };

    private sealed class SearchStore(params (string Id, PipelineRuleDocument Rule)[] rules)
        : IElasticsearchDocumentClient, IElasticsearchPointInTimeClient
    {
        public List<string> OpenedIndexes { get; } = [];
        public List<string> ClosedPits { get; } = [];
        public List<ElasticsearchPointInTimeSearchRequest> Requests { get; } = [];
        public int SyntheticCount { get; init; }
        public int FailRead { get; init; }
        public Action? BeforeRead { get; init; }
        public bool CloseTokenWasCancelled { get; private set; }

        public Task<string> OpenPointInTimeAsync(string indexName, string keepAlive, CancellationToken cancellationToken = default)
        {
            OpenedIndexes.Add(indexName);
            return Task.FromResult("pit-1");
        }

        public Task<ElasticsearchSearchPage<T>> SearchPointInTimeAsync<T>(ElasticsearchPointInTimeSearchRequest request,
            CancellationToken cancellationToken = default) where T : class
        {
            Requests.Add(request);
            BeforeRead?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            if (Requests.Count == FailRead) throw new ElasticsearchClientException("search failed");
            var offset = request.SearchAfter.Count == 0 ? 0 : request.SearchAfter[0].GetInt32() + 1;
            if (SyntheticCount > 0)
            {
                var generated = Enumerable.Range(offset, Math.Min(request.Search.Size, SyntheticCount - offset))
                    .Select(i => new ElasticsearchSearchHit<T>($"rule-{i}",
                        JsonSerializer.Deserialize<T>("{\"ruleName\":\"ordinary\"}")!, [JsonSerializer.SerializeToElement(i)]))
                    .ToArray();
                return Task.FromResult(new ElasticsearchSearchPage<T>($"pit-{Requests.Count + 1}",
                    request.TrackTotalHits ? SyntheticCount : null, generated));
            }
            var all = rules.Where(rule => request.Search.TermFilters.All(filter => Equals(rule.Item2.IsActive, filter.Value)));
            if (request.Search.TermsFilters.Count > 0)
            {
                var ids = request.Search.TermsFilters.Single().Values.Cast<string>().ToHashSet();
                all = all.Where(rule => ids.Contains(rule.Item1));
            }
            var hits = all.Skip(offset).Take(request.Search.Size).Select((rule, i) =>
            {
                var value = request.Search.SourceIncludes.Count == 0 ? rule.Item2 : new PipelineRuleDocument
                {
                    RuleName = rule.Item2.RuleName, Description = rule.Item2.Description, Match = rule.Item2.Match
                };
                return new ElasticsearchSearchHit<T>(rule.Item1,
                    JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!, [JsonSerializer.SerializeToElement(offset + i)]);
            }).ToArray();
            long? total = request.TrackTotalHits ? all.Count() : null;
            return Task.FromResult(new ElasticsearchSearchPage<T>($"pit-{Requests.Count + 1}", total, hits));
        }

        public Task ClosePointInTimeAsync(string pointInTimeId, CancellationToken cancellationToken = default)
        {
            ClosedPits.Add(pointInTimeId);
            CloseTokenWasCancelled = cancellationToken.IsCancellationRequested;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<ElasticsearchDocument<T>>> SearchDocumentsAsync<T>(ElasticsearchSearchRequest request,
            CancellationToken cancellationToken = default) where T : class => Task.FromResult<IReadOnlyList<ElasticsearchDocument<T>>>(
                rules.Skip(request.From).Take(request.Size).Select(rule => new ElasticsearchDocument<T>(rule.Id,
                    JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(rule.Rule))!)).ToArray());

        public Task<IReadOnlyList<T>> SearchAsync<T>(ElasticsearchSearchRequest request) where T : class => throw new NotSupportedException();
        public Task<IReadOnlyList<T>> SearchBySensorAsync<T>(ElasticsearchSensorSearchRequest request) where T : class => throw new NotSupportedException();
        public Task<IReadOnlyList<T>> SearchByGeoShapeAsync<T>(ElasticsearchGeoShapeSearchRequest request) where T : class => throw new NotSupportedException();
        public Task<T?> GetAsync<T>(string indexName, string id) where T : class => throw new NotSupportedException();
        public Task<ElasticsearchDocument<T>?> GetDocumentAsync<T>(string indexName, string id, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task<IReadOnlyList<ElasticsearchDocument<T>>> SearchDocumentsAsync<T>(Func<SearchDescriptor<T>, ISearchRequest> configure,
            CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task<string> IndexAsync<T>(string indexName, string? id, T document, bool waitForRefresh = true,
            bool allowGeneratedId = false, CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
        public Task<bool> DeleteAsync<T>(string indexName, string id, bool waitForRefresh = true,
            CancellationToken cancellationToken = default) where T : class => throw new NotSupportedException();
    }
}
