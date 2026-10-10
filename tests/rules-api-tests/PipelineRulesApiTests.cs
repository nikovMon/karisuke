using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelineRulesApiTests
{
    private const string AsdIndex = "asd-integ-pipeline-index";
    private const string AlgoIndex = "algo-integ-pipeline-index";

    [Fact]
    public async Task GetAllReadsOnlyThePipelinesOwnIndex()
    {
        var store = new InMemoryIndexStore();
        store.Add(AsdIndex, "asd-rule", Rule("asd rule"));
        store.Add(AlgoIndex, "algo-rule", Rule("algo rule"));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var rules = await client.GetFromJsonAsync<List<PipelineRuleDocument>>("/pipelines/asd/rules");

        var rule = Assert.Single(rules!);
        Assert.Equal("asd-rule", rule.Id);
        Assert.Equal("asd rule", rule.RuleName);
    }

    [Fact]
    public async Task GetAllFiltersByActivityAndCanReturnNamesOnly()
    {
        var store = new InMemoryIndexStore();
        store.Add(AsdIndex, "active", Rule("active"));
        store.Add(AsdIndex, "inactive", Rule("inactive", isActive: false));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var names = await client.GetFromJsonAsync<List<string>>("/pipelines/asd/rules?getNameOnly=true&isActive=false");

        Assert.Equal(["inactive"], names);
    }

    [Fact]
    public async Task GetByIdAndByNameReturnTheRuleOrNotFound()
    {
        var store = new InMemoryIndexStore();
        store.Add(AsdIndex, "rule-1", Rule("first"));
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var byId = await client.GetFromJsonAsync<PipelineRuleDocument>("/pipelines/asd/rules/rule-1");
        var byName = await client.GetFromJsonAsync<PipelineRuleDocument>("/pipelines/asd/rules/name/first");
        var missing = await client.GetAsync("/pipelines/asd/rules/unknown");
        var otherPipeline = await client.GetAsync("/pipelines/algo/rules/rule-1");

        Assert.Equal("first", byId!.RuleName);
        Assert.Equal("rule-1", byName!.Id);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, otherPipeline.StatusCode);
    }

    [Theory]
    [InlineData("/pipelines/unknown/rules")]
    [InlineData("/pipelines/unknown/rules/rule-1")]
    [InlineData("/pipelines/unknown/rules/name/first")]
    public async Task UnknownPipelineIsNotFoundWithoutReachingElasticsearch(string path)
    {
        var store = new InMemoryIndexStore();
        using var factory = new RulesApiFactory(store);
        using var client = factory.CreateClient();

        var response = await client.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(0, store.CallCount);
    }

    [Theory]
    [InlineData("?from=-1")]
    [InlineData("?size=0")]
    [InlineData("?size=100000")]
    public async Task InvalidPageIsRejected(string query)
    {
        using var factory = new RulesApiFactory(new InMemoryIndexStore());
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/asd/rules" + query);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static PipelineRuleDocument Rule(string name, bool isActive = true) => new()
    {
        RuleName = name,
        IsActive = isActive,
        MatchAll = true,
        RunParams = [JsonSerializer.SerializeToElement(new { tenantId = "tenant-1" })]
    };
}
