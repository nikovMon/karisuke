using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelineRuleBulkApiTests : IDisposable
{
    private const string AsdIndex = "asd-integ-pipeline-index";

    private readonly InMemoryIndexStore _store = new();
    private readonly RulesApiFactory _factory;

    public PipelineRuleBulkApiTests()
    {
        _factory = new RulesApiFactory(_store);
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task BulkUpdateAppliesTheChangeToEveryRule()
    {
        _store.Add(AsdIndex, "a", Rule("a"));
        _store.Add(AsdIndex, "b", Rule("b"));
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync("/pipelines/asd/rules/bulk?ids=a,b", new { match = new { maxPhotoAgeDays = 7 } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["a", "b"], (await response.Content.ReadFromJsonAsync<BulkOperationResult>())!.SuccessIds);
        Assert.Equal(7, _store.Stored(AsdIndex, "b")!["match"]!["maxPhotoAgeDays"]!.GetValue<int>());
    }

    [Fact]
    public async Task BulkUpdateReportsEachFailedRule()
    {
        _store.Add(AsdIndex, "a", Rule("a"));
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync("/pipelines/asd/rules/bulk?ids=a,missing", new { isActive = false });

        Assert.Equal(HttpStatusCode.MultiStatus, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<BulkOperationResult>();
        Assert.Equal(["a"], result!.SuccessIds);
        Assert.Equal("missing", Assert.Single(result.FailedIds).Id);
    }

    [Fact]
    public async Task BulkUpdateCannotGiveSeveralRulesOneName()
    {
        _store.Add(AsdIndex, "a", Rule("a"));
        _store.Add(AsdIndex, "b", Rule("b"));
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync("/pipelines/asd/rules/bulk?ids=a,b", new { ruleName = "same" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    [Fact]
    public async Task AddSensorsMergesValuesIntoTheSensor()
    {
        _store.Add(AsdIndex, "a", Rule("a"));
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/pipelines/asd/rules/sensors/add?ids=a",
            new { sensorName = "camera", gridTypes = new[] { "grid-a" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sensor = Assert.Single(Stored("a").Match!.Sensors!);
        Assert.Equal([RegistrationQuality.Accurate], sensor.RegistrationQualities);
        Assert.Equal(["grid-a"], sensor.GridTypes);
    }

    [Fact]
    public async Task AddSensorsToAMatchAllRuleFailsForThatRule()
    {
        _store.Add(AsdIndex, "all", new PipelineRuleDocument { RuleName = "all", MatchAll = true, RunParams = Rule("x").RunParams });
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/pipelines/asd/rules/sensors/add?ids=all",
            new { sensorName = "camera", gridTypes = new[] { "grid-a" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Null(Stored("all").Match);
    }

    [Fact]
    public async Task RemoveSensorsKeepsTheSensorWhileItHasValues()
    {
        var rule = Rule("a");
        rule.Match!.Sensors![0].GridTypes = ["grid-a"];
        _store.Add(AsdIndex, "a", rule);
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/pipelines/asd/rules/sensors/remove?ids=a",
            new { sensorName = "camera", gridTypes = new[] { "grid-a" } });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(Assert.Single(Stored("a").Match!.Sensors!).GridTypes);
    }

    [Fact]
    public async Task RemovingTheLastSensorIsRefusedSoTheRuleDoesNotWidenToEverySensor()
    {
        _store.Add(AsdIndex, "a", Rule("a"));
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync(
            "/pipelines/asd/rules/sensors/remove?ids=a",
            new { sensorName = "camera", registrationQualities = new[] { "Accurate" } });

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        var failure = Assert.Single((await response.Content.ReadFromJsonAsync<BulkOperationResult>())!.FailedIds);
        Assert.Contains("last sensor", failure.Reason, StringComparison.Ordinal);
        Assert.Single(Stored("a").Match!.Sensors!);
    }

    [Theory]
    [InlineData("/pipelines/asd/rules/bulk")]
    [InlineData("/pipelines/asd/rules/sensors/add")]
    public async Task MissingIdsAreRejected(string path)
    {
        using var client = CreateClient();

        var response = await client.PatchAsJsonAsync(path + "?ids=", new { sensorName = "camera", gridTypes = new[] { "g" } });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private HttpClient CreateClient() => _factory.CreateClient();

    private PipelineRuleDocument Stored(string id) =>
        _store.Stored(AsdIndex, id)!.Deserialize<PipelineRuleDocument>()!;

    private static PipelineRuleDocument Rule(string name) => new()
    {
        RuleName = name,
        Match = new RuleMatchConditions
        {
            Sensors = [new SensorConfig { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate] }]
        },
        RunParams =
        [
            JsonSerializer.SerializeToElement(new
            {
                tenantId = "tenant-1",
                algorithmNames = new[] { "FindAir" },
                tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
            })
        ]
    };
}
