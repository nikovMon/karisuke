using System.Globalization;
using System.Text.Json;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class OverlayDispatchContextFactoryTests
{
    [Fact]
    public void FlatOverlayPreservesIdsRoiAndLegacyPhotoTimeIndependentlyOfAsdUtcTime()
    {
        const string photoTime = "2026-09-23T00:15:00+03:00";
        PipelineDispatchContext context;
        using (var document = JsonDocument.Parse("""
            {
              "id":"image-a","photoTime":"2026-09-23T00:15:00+03:00",
              "legId":"leg-a","prevOverlayId":null,"nextOverlayId":"image-b",
              "roiFootprint":{"type":"Point","coordinates":[1,2]},
              "footprint":{"type":"Point","coordinates":[3,4]},
              "bestResolution":0.5,"gridType":"grid","sensorName":"sensor",
              "sensorType":"EO","isRealTime":true,"channelsPerPixel":3
            }
            """))
        {
            context = OverlayDispatchContextFactory.Create("task", "rule", document.RootElement);
        }

        Assert.Equal("image-a", context.ImageId);
        Assert.Equal("leg-a", context.LegId);
        Assert.Null(context.PrevOverlayId);
        Assert.Equal("image-b", context.NextOverlayId);
        Assert.Equal(1, context.RoiFootprint.GetProperty("coordinates")[0].GetInt32());
        var legacyTime = DateTime.Parse(photoTime, CultureInfo.InvariantCulture);
        Assert.Equal(legacyTime, context.OverlayPhotoTime);
        Assert.Equal(legacyTime.Kind, context.OverlayPhotoTime!.Value.Kind);
        Assert.Equal(TimeSpan.Zero, context.PhotoTime.Offset);
        Assert.Equal(22, context.PhotoTime.Day);
    }

    [Fact]
    public void AlgoOverlayDoesNotRequireAsdMetadataButAsdContractRejectsMissingValues()
    {
        var context = Context("""{"id":"image-a","photoTime":"2026-09-23T10:00:00Z"}""") with
        {
            RuleLocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 0))"
        };
        Assert.Empty(new AlgoPipelineContract().ValidateContext(context));
        var errors = new AsdPipelineContract().ValidateContext(context);
        Assert.Contains(errors, error => error.Field == "input.imageUrl");
        Assert.Contains(errors, error => error.Field == "input.width");
        Assert.Contains(errors, error => error.Field == "input.gridURI");
    }

    [Fact]
    public void FullImageFootprintIsNeverSilentlyUsedAsRoi()
    {
        var context = Context("""
            {"id":"image-a","photoTime":"2026-09-23T10:00:00Z",
             "footprint":{"type":"Point","coordinates":[3,4]}}
            """);
        Assert.Equal(JsonValueKind.Undefined, context.RoiFootprint.ValueKind);
    }

    [Theory]
    [InlineData("  example-area  ", "example-area")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    public void AreaOfInterestKeepsExistingAsdWhitespaceNormalization(string? area, string? expected)
    {
        var overlay = JsonSerializer.SerializeToElement(new { id = "image", photoTime = "2026-09-23T10:00:00Z", areaOfInterest = area });
        Assert.Equal(expected, OverlayDispatchContextFactory.Create("task", "rule", overlay).AreaOfInterest);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"id\":\"x\",\"photoTime\":\"bad-date\"}")]
    [InlineData("{\"id\":\"x\",\"id\":\"y\",\"photoTime\":\"2026-09-23\"}")]
    [InlineData("{\"id\":\"x\",\"photoTime\":\"2026-09-23\",\"legId\":42}")]
    [InlineData("{\"id\":\"x\",\"photoTime\":\"2026-09-23\",\"width\":\"10\"}")]
    [InlineData("{\"id\":\"x\",\"photoTime\":\"2026-09-23\",\"bestResolution\":1e400}")]
    public void InvalidInputFailsBeforePayloadConstruction(string json) =>
        Assert.Throws<ArgumentException>(() => Context(json));

    private static PipelineDispatchContext Context(string json)
    {
        using var document = JsonDocument.Parse(json);
        return OverlayDispatchContextFactory.Create("task", "rule", document.RootElement);
    }
}
