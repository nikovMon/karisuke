using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.RuleEngine.Tests;

internal static class RuleTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static RuleDto Rule(string id = "rule-1") => new()
    {
        Id = id,
        RuleName = id,
        AlgorithmNames = [AlgorithmName.FindAir],
        Sensors = [new SensorConfig { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate] }],
        TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = "tenant-1",
                TilingConfigs = [new TilingConfig { TileSizeWidth = 512, TileSizeHeight = 512 }]
            }
        ],
        MinimumResolution = 0.5,
        MaximumResolution = 1,
        LocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))"
    };

    public static ActiveRule ActiveRule(RuleDto? rule = null) =>
        Assert.Single(new RuleSnapshotBuilder(TimeSpan.FromDays(30))
            .Build(new([rule ?? Rule()], [])).Rules);

    public static GatewayInputMessage Image(
        string sensorName = "camera",
        RegistrationQuality quality = RegistrationQuality.Accurate,
        double bestResolution = 0.7,
        string gridType = "grid-a",
        DateTimeOffset? photoTime = null,
        string footprintWkt = "POLYGON ((1 1, 3 1, 3 3, 1 3, 1 1))") =>
        new(
            "image-1",
            sensorName,
            "EO",
            quality,
            bestResolution,
            null,
            "https://example.invalid/image",
            100,
            100,
            photoTime ?? Now.AddDays(-1),
            GeometryUtilities.ReadWkt(footprintWkt),
            gridType,
            "https://example.invalid/grid");
}
