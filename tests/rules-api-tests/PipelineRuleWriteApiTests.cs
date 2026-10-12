using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelineRuleWriteApiTests
{
    private const string AsdIndex = "asd-integ-pipeline-index";
    private const string Square = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))";

    private readonly InMemoryIndexStore _store = new();

    [Fact]
    public async Task CreateStoresAValidRuleInThePipelinesIndexWithDerivedLocation()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();

        var response = await client.PostAsJsonAsync("/pipelines/asd/rules", NewRule("first"));

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var rule = await response.Content.ReadFromJsonAsync<PipelineRuleDocument>();
        Assert.False(string.IsNullOrEmpty(rule!.Id));
        Assert.EndsWith($"/pipelines/asd/rules/{rule.Id}", response.Headers.Location!.ToString(), StringComparison.Ordinal);
        var stored = _store.Stored(AsdIndex, rule.Id)!;
        Assert.Equal("Polygon", stored["match"]!["location"]!["type"]!.GetValue<string>());
        Assert.Null(stored["_id"]);
        Assert.Equal(PipelineRuleDocument.CurrentSchemaVersion, stored["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public async Task CreateRejectsRunParamsTheContractDoesNotAccept()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var request = NewRule("first", tenantId: " ");

        var response = await client.PostAsJsonAsync("/pipelines/asd/rules", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("runParams[0].tenantId", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.Equal(0, _store.CallCount);
    }

    [Fact]
    public async Task CreateAppliesTheContractsLocationRequirement()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var request = NewRule("first");
        request.Match = null;
        request.MatchAll = true;

        var response = await client.PostAsJsonAsync("/pipelines/algo/rules", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("match.locationWkt", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RuleNamesAreUniqueWithinAPipeline()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        await client.PostAsJsonAsync("/pipelines/asd/rules", NewRule("first"));

        var duplicate = await client.PostAsJsonAsync("/pipelines/asd/rules", NewRule("first"));

        Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
    }

    [Fact]
    public async Task UpdatePatchesOneConditionAndKeepsTheOthers()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, "first");

        var response = await client.PatchAsJsonAsync(
            $"/pipelines/asd/rules/{id}",
            new { match = new { maxPhotoAgeDays = 7 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rule = await response.Content.ReadFromJsonAsync<PipelineRuleDocument>();
        Assert.Equal(7, rule!.Match!.MaxPhotoAgeDays);
        Assert.Equal(Square, rule.Match.LocationWkt);
        Assert.Equal("camera", Assert.Single(rule.Match.Sensors!).Name);
    }

    [Fact]
    public async Task UpdateRemovingTheLocationAlsoRemovesTheDerivedGeoJson()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, "first");

        var response = await client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}", new { match = new { locationWkt = (string?)null } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Null(_store.Stored(AsdIndex, id)!["match"]!["location"]);
    }

    [Fact]
    public async Task UpdateThatLeavesAnInvalidRuleIsRejectedAndNothingIsSaved()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, "first");

        var response = await client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}", new { match = (object?)null });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("matchAll", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.NotNull(_store.Stored(AsdIndex, id)!["match"]);
    }

    [Fact]
    public async Task ActivityAndDeleteChangeTheStoredRule()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();
        var id = await CreateAsync(client, "first");

        var deactivated = await client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}/activity", new { isActive = false });
        var isActive = _store.Stored(AsdIndex, id)!["isActive"]!.GetValue<bool>();
        var deleted = await client.DeleteAsync($"/pipelines/asd/rules/{id}");
        var deletedAgain = await client.DeleteAsync($"/pipelines/asd/rules/{id}");

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False(isActive);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        Assert.Null(_store.Stored(AsdIndex, id));
        Assert.Equal(HttpStatusCode.NotFound, deletedAgain.StatusCode);
    }

    [Fact]
    public async Task RuleThatNoLongerValidatesCanStillBeDeactivatedButNotReactivated()
    {
        _store.Add(AsdIndex, "stale", new PipelineRuleDocument
        {
            RuleName = "stale",
            MatchAll = true,
            RunParams = [JsonSerializer.SerializeToElement(new { tenantId = " " })]
        });
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();

        var deactivated = await client.PatchAsJsonAsync("/pipelines/asd/rules/stale/activity", new { isActive = false });
        var reactivated = await client.PatchAsJsonAsync("/pipelines/asd/rules/stale/activity", new { isActive = true });

        Assert.Equal(HttpStatusCode.OK, deactivated.StatusCode);
        Assert.False(_store.Stored(AsdIndex, "stale")!["isActive"]!.GetValue<bool>());
        Assert.Equal(HttpStatusCode.BadRequest, reactivated.StatusCode);
    }

    [Fact]
    public async Task WritesToAnUnknownRuleAreNotFound()
    {
        using var factory = new RulesApiFactory(_store);
        using var client = factory.CreateClient();

        var update = await client.PatchAsJsonAsync("/pipelines/asd/rules/missing", new { ruleName = "x" });
        var activity = await client.PatchAsJsonAsync("/pipelines/asd/rules/missing/activity", new { isActive = true });

        Assert.Equal(HttpStatusCode.NotFound, update.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, activity.StatusCode);
    }

    private async Task<string> CreateAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/pipelines/asd/rules", NewRule(name));
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PipelineRuleDocument>())!.Id;
    }

    private static CreatePipelineRuleRequest NewRule(string name, string tenantId = "tenant-1") => new()
    {
        RuleName = name,
        Match = new RuleMatchConditions
        {
            Sensors = [new SensorConfig { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate] }],
            LocationWkt = Square
        },
        RunParams =
        [
            JsonSerializer.SerializeToElement(new
            {
                tenantId,
                algorithmNames = new[] { "FindAir" },
                tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
            })
        ]
    };
}
