using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.RuleEngine.Tests;

internal static class RuleTestData
{
    public static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    public static readonly IPipelineContract Contract = new AsdPipelineContract();

    public static PipelineRuleDocument Rule(string id = "rule-1") => new()
    {
        Id = id,
        RuleName = id,
        Match = new RuleMatchConditions
        {
            Sensors = [new SensorConfig { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate] }],
            Resolution = new ResolutionRange { Min = 0.5, Max = 1 },
            LocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))"
        },
        RunParams = [AsdRunParams("tenant-1")]
    };

    public static JsonElement AsdRunParams(string tenantId) => JsonSerializer.SerializeToElement(new
    {
        tenantId,
        algorithmNames = new[] { "FindAir" },
        tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
    });

    public static ActiveRule ActiveRule(PipelineRuleDocument? rule = null) =>
        Assert.Single(RuleSnapshotBuilder.Build(new([rule ?? Rule()], []), Contract).Rules);

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
