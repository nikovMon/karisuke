using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Rules.Api.Tests.Fakes;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class PipelineRuleAuditTests : IDisposable
{
    private const string AuditIndex = "rules-audit";

    private readonly InMemoryIndexStore _store = new();
    private readonly RulesApiFactory _factory;
    private readonly HttpClient _client;

    public PipelineRuleAuditTests()
    {
        _factory = new RulesApiFactory(_store);
        _client = _factory.CreateClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    [Fact]
    public async Task EveryChangeAppendsARecordWithItsStates()
    {
        var id = await CreateAsync();
        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}", new { description = "edited" });
        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}/activity", new { isActive = false });
        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}/activity", new { isActive = true });
        await _client.DeleteAsync($"/pipelines/asd/rules/{id}");

        var records = Records();

        Assert.Equal(
            [RuleChangeType.Created, RuleChangeType.Updated, RuleChangeType.Deactivated, RuleChangeType.Activated, RuleChangeType.Deleted],
            records.Select(record => record.ChangeType));
        Assert.All(records, record =>
        {
            Assert.Equal("asd", record.PipelineId);
            Assert.Equal(id, record.RuleId);
            Assert.Null(record.ChangedBy);
        });
        Assert.Null(records[0].PreviousState);
        Assert.Equal("first", records[0].NewState!.Value.GetProperty("ruleName").GetString());
        Assert.False(records[0].NewState!.Value.TryGetProperty("_id", out _));
        Assert.Equal(["description"], records[1].ChangedFields);
        Assert.False(records[1].PreviousState!.Value.TryGetProperty("description", out _));
        Assert.Equal("edited", records[1].NewState!.Value.GetProperty("description").GetString());
        Assert.Equal(["isActive"], records[2].ChangedFields);
        Assert.Equal("edited", records[4].PreviousState!.Value.GetProperty("description").GetString());
        Assert.Null(records[4].NewState);
    }

    [Fact]
    public async Task SensorChangesAreAuditedPerRuleAndNoOpsAreNot()
    {
        var id = await CreateAsync();

        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/sensors/add?ids={id}", new { sensorName = "camera", gridTypes = new[] { "grid-a" } });
        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/sensors/add?ids={id}", new { sensorName = "camera", gridTypes = new[] { "grid-a" } });

        var records = Records();
        Assert.Equal(2, records.Count);
        Assert.Equal(["match.sensors"], records[1].ChangedFields);
    }

    [Fact]
    public async Task ChangedFieldsAndChangeTypeComeFromWhatActuallyChanged()
    {
        var id = await CreateAsync();

        await _client.PatchAsJsonAsync(
            $"/pipelines/asd/rules/{id}",
            new { ruleName = "first", isActive = false, match = new { locationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))" } });

        var record = Records()[1];
        Assert.Equal(["isActive", "match.locationWkt"], record.ChangedFields);
        Assert.Equal(RuleChangeType.Updated, record.ChangeType);
        Assert.False(string.IsNullOrEmpty(record.RequestId));
    }

    [Fact]
    public async Task ResendingUnchangedFieldsWithADeactivationIsADeactivation()
    {
        var id = await CreateAsync();

        await _client.PatchAsJsonAsync($"/pipelines/asd/rules/{id}", new { ruleName = "first", isActive = false });

        var record = Records()[1];
        Assert.Equal(RuleChangeType.Deactivated, record.ChangeType);
        Assert.Equal(["isActive"], record.ChangedFields);
    }

    [Fact]
    public async Task AFailedAuditWriteDoesNotFailTheChange()
    {
        _store.FailingIndexes.Add(AuditIndex);

        var response = await _client.PostAsJsonAsync("/pipelines/asd/rules", NewRule());

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Empty(_store.All<RuleAuditRecord>(AuditIndex));
    }

    private List<RuleAuditRecord> Records() =>
        _store.All<RuleAuditRecord>(AuditIndex).OrderBy(record => record.ChangedAt).ToList();

    private async Task<string> CreateAsync()
    {
        var response = await _client.PostAsJsonAsync("/pipelines/asd/rules", NewRule());
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<PipelineRuleDocument>())!.Id;
    }

    private static CreatePipelineRuleRequest NewRule() => new()
    {
        RuleName = "first",
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
