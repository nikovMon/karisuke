using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineContracts;

namespace ImagingPipeline.RuleMigrator.Tests;

public sealed class RuleMigrationTests
{
    private const string Source = "rules";
    private const string Target = "findair-rules";
    private static readonly MigrationTarget FindAir = new(Target, new FindAirPipelineContract());

    private readonly FakeDocumentClient _client = new();

    public RuleMigrationTests() => _client.CreateIndex(Target);

    [Fact]
    public async Task DryRunReportsWithoutWriting()
    {
        _client.Add(Source, "r1", V1Rule("north"));
        _client.Add(Source, "r2", V1Rule("south", tenants: []));

        var report = await RunAsync(apply: false);

        Assert.Equal(2, report.Read);
        Assert.Equal(["r1"], report.Migrated);
        var skipped = Assert.Single(report.Skipped);
        Assert.Equal(("r2", "south"), (skipped.Id, skipped.RuleName));
        Assert.StartsWith("runParams", skipped.Reason, StringComparison.Ordinal);
        Assert.Empty(_client.Saved);
    }

    [Fact]
    public async Task ApplyWritesUnderTheSameIdWithDerivedLocation()
    {
        _client.Add(Source, "r1", V1Rule("north"));

        await RunAsync(apply: true);

        var (index, id, source) = Assert.Single(_client.Saved);
        Assert.Equal(Target, index);
        Assert.Equal("r1", id);
        Assert.Equal(2, (int)source["schemaVersion"]!);
        Assert.Equal("Polygon", (string?)source["match"]!["location"]!["type"]);
        Assert.Equal(7, (int)source["match"]!["maxPhotoAgeDays"]!);
    }

    [Fact]
    public async Task RuleNamedLikeAnotherTargetRuleIsSkipped()
    {
        _client.Add(Target, "v2-rule", new { ruleName = "north" });
        _client.Add(Target, "r2", new { ruleName = "south" });
        _client.Add(Source, "r1", V1Rule("north"));
        _client.Add(Source, "r2", V1Rule("south"));

        var report = await RunAsync(apply: true);

        Assert.Equal(["r2"], report.Migrated);
        Assert.Equal("ruleName is already used by rule 'v2-rule'", Assert.Single(report.Skipped).Reason);
    }

    [Fact]
    public async Task DuplicateNamesInTheSourceMigrateOnce()
    {
        _client.Add(Source, "r1", V1Rule("north"));
        _client.Add(Source, "r2", V1Rule("north"));

        var report = await RunAsync(apply: true);

        Assert.Equal(["r1"], report.Migrated);
        Assert.Equal("r2", Assert.Single(report.Skipped).Id);
    }

    [Fact]
    public async Task UnreadableAndUnsavedRulesAreSkippedAndTheRestMigrate()
    {
        _client.Add(Source, "bad", """{ "ruleName": "bad", "algorithmName": ["Unknown"] }""");
        _client.Add(Source, "r1", V1Rule("north"));
        _client.Add(Source, "r2", V1Rule("south"));
        _client.FailingSaveIds.Add("r1");

        var report = await RunAsync(apply: true);

        Assert.Equal(["r2"], report.Migrated);
        Assert.Collection(
            report.Skipped,
            unreadable => Assert.StartsWith("not a readable v1 rule", unreadable.Reason, StringComparison.Ordinal),
            unsaved => Assert.Equal("save failed: save 'r1' failed", unsaved.Reason));
    }

    [Fact]
    public async Task ReportsDroppedAreas()
    {
        _client.Add(Source, "r1", V1Rule("north", area: "north area"));
        _client.Add(Source, "r2", V1Rule("south", area: ""));

        var report = await RunAsync(apply: false);

        Assert.Equal(new DroppedArea("r1", "north", "north area"), Assert.Single(report.DroppedAreas));
    }

    [Fact]
    public async Task ReadsEveryPage()
    {
        for (var index = 0; index < 501; index++)
        {
            _client.Add(Source, $"r{index}", V1Rule($"rule-{index}"));
        }

        var report = await RunAsync(apply: false);

        Assert.Equal(501, report.Read);
        Assert.Equal(501, report.Migrated.Count);
    }

    [Fact]
    public async Task MissingTargetIndexFails()
    {
        var migration = new RuleMigration(_client);

        await Assert.ThrowsAsync<ElasticsearchClientException>(() => migration.RunAsync(
            new MigrationTarget("missing", new FindAirPipelineContract()),
            new MigrationOptions { Pipeline = "FindAir" },
            CancellationToken.None));
    }

    private Task<MigrationReport> RunAsync(bool apply) =>
        new RuleMigration(_client).RunAsync(
            FindAir,
            new MigrationOptions { Pipeline = "FindAir", SourceIndex = Source, Apply = apply },
            CancellationToken.None);

    private static object V1Rule(string name, object[]? tenants = null, string area = "") => new
    {
        ruleName = name,
        algorithmName = new[] { "FindAir" },
        sensors = new[] { new { name = "S1", registrationQualities = new[] { "Accurate" }, gridTypes = new[] { "A" } } },
        isActive = true,
        tenantsInfo = tenants ?? [new
        {
            tenantId = "t1",
            tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
        }],
        minimumResolution = 0.5,
        maximumResolution = 999,
        area,
        locationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))",
        isPhotoOld = true,
        creationTime = "2026-01-01T00:00:00Z",
        updateTime = "2026-01-02T00:00:00Z"
    };
}
