using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.RuleMigrator.Tests;

public sealed class RuleConverterTests
{
    private const int MaxPhotoAgeDays = 7;

    [Fact]
    public void CopiesRuleFields()
    {
        var v1 = V1Rule();

        var rule = RuleConverter.Convert(v1, MaxPhotoAgeDays);

        Assert.Equal("rule-1", rule.Id);
        Assert.Equal(PipelineRuleDocument.CurrentSchemaVersion, rule.SchemaVersion);
        Assert.Equal("north", rule.RuleName);
        Assert.Equal("north area", rule.Description);
        Assert.False(rule.IsActive);
        Assert.False(rule.MatchAll);
        Assert.Equal(v1.CreationTime, rule.CreationTime);
        Assert.Equal(v1.UpdateTime, rule.UpdateTime);
        Assert.Equal(RuleConverter.MigrationActor, rule.CreatedBy);
        Assert.Equal(RuleConverter.MigrationActor, rule.UpdatedBy);
    }

    [Fact]
    public void MapsConditionsToMatch()
    {
        var v1 = V1Rule();

        var match = RuleConverter.Convert(v1, MaxPhotoAgeDays).Match!;

        Assert.Same(v1.Sensors, match.Sensors);
        Assert.Equal(0.3, match.Resolution!.Min);
        Assert.Equal(999, match.Resolution.Max);
        Assert.Equal(v1.LocationWkt, match.LocationWkt);
        Assert.Null(match.Location);
        Assert.Equal(MaxPhotoAgeDays, match.MaxPhotoAgeDays);
    }

    [Fact]
    public void EmptySensorsMeanAnySensor()
    {
        var v1 = V1Rule();
        v1.Sensors = [];

        Assert.Null(RuleConverter.Convert(v1, MaxPhotoAgeDays).Match!.Sensors);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void PhotoAgeIsSetOnlyForOldPhotoRules(bool? isPhotoOld)
    {
        var v1 = V1Rule();
        v1.IsPhotoOld = isPhotoOld;

        Assert.Null(RuleConverter.Convert(v1, MaxPhotoAgeDays).Match!.MaxPhotoAgeDays);
    }

    [Fact]
    public void EachTenantBecomesRunParamsWithEveryAlgorithm()
    {
        var v1 = V1Rule();
        v1.TenantsInfo.Add(new TenantInfo { TenantId = "tenant-2", TilingConfigs = [] });

        var runParams = RuleConverter.Convert(v1, MaxPhotoAgeDays).RunParams!;

        Assert.Collection(
            runParams,
            first => Assert.Equal(
                """{"tenantId":"tenant-1","algorithmNames":["FindAir","Rpn"],"tilingConfigs":[{"tileSizeWidth":512,"tileSizeHeight":256,"tileOverlapWidth":16,"tileOverlapHeight":8}]}""",
                first.GetRawText()),
            second => Assert.Equal(
                """{"tenantId":"tenant-2","algorithmNames":["FindAir","Rpn"],"tilingConfigs":[]}""",
                second.GetRawText()));
    }

    [Fact]
    public void ConvertedRulePassesFindAirValidation()
    {
        var rule = RuleConverter.Convert(V1Rule(), MaxPhotoAgeDays);

        var validation = PipelineRuleValidator.Validate(rule, new FindAirPipelineContract());

        Assert.Empty(validation.Errors);
    }

    [Fact]
    public void RuleWithoutTenantsFailsValidation()
    {
        var v1 = V1Rule();
        v1.TenantsInfo = [];

        var rule = RuleConverter.Convert(v1, MaxPhotoAgeDays);

        Assert.Empty(rule.RunParams!);
        Assert.Contains(
            PipelineRuleValidator.Validate(rule, new FindAirPipelineContract()).Errors,
            error => error.StartsWith("runParams", StringComparison.Ordinal));
    }

    [Fact]
    public void ConvertsStoredV1Json()
    {
        var v1 = JsonSerializer.Deserialize<RuleDto>(
            """
            {
              "ruleName": "stored",
              "algorithmName": ["FindAir"],
              "sensors": [{ "name": "S1", "registrationQualities": ["Accurate"], "gridTypes": ["A"] }],
              "isActive": true,
              "tenantsInfo": [{ "tenantId": "t1", "tilingConfigs": [{ "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 0, "tileOverlapHeight": 0 }] }],
              "minimumResolution": 0.5,
              "maximumResolution": 2,
              "area": "north",
              "locationWkt": "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))",
              "locationGeoJson": { "type": "Polygon", "coordinates": [] },
              "creationTime": "2026-01-01T00:00:00Z",
              "updateTime": "2026-01-02T00:00:00Z"
            }
            """)!;

        var rule = RuleConverter.Convert(v1, MaxPhotoAgeDays);

        Assert.Empty(PipelineRuleValidator.Validate(rule, new FindAirPipelineContract()).Errors);
        Assert.Null(rule.Match!.MaxPhotoAgeDays);
        Assert.Equal(
            """{"tenantId":"t1","algorithmNames":["FindAir"],"tilingConfigs":[{"tileSizeWidth":512,"tileSizeHeight":512,"tileOverlapWidth":0,"tileOverlapHeight":0}]}""",
            Assert.Single(rule.RunParams!).GetRawText());
    }

    private static RuleDto V1Rule() => new()
    {
        Id = "rule-1",
        RuleName = "north",
        Description = "north area",
        AlgorithmNames = [AlgorithmName.FindAir, AlgorithmName.Rpn],
        Sensors = [new SensorConfig { Name = "S1", RegistrationQualities = [RegistrationQuality.Accurate], GridTypes = ["A"] }],
        IsActive = false,
        TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = "tenant-1",
                TilingConfigs = [new TilingConfig { TileSizeWidth = 512, TileSizeHeight = 256, TileOverlapWidth = 16, TileOverlapHeight = 8 }]
            }
        ],
        MinimumResolution = 0.3,
        MaximumResolution = 999,
        Area = "north",
        LocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))",
        IsPhotoOld = true,
        CreationTime = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        UpdateTime = new DateTimeOffset(2026, 2, 1, 0, 0, 0, TimeSpan.Zero)
    };
}
