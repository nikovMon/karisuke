using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.RuleMigrator.Tests;

public sealed class AlgoRuleConverterTests
{
    private const int MaxPhotoAgeDays = 2;

    [Fact]
    public void MapsEveryField()
    {
        var rule = AlgoRuleConverter.Convert(Algo(), MaxPhotoAgeDays);

        Assert.Equal("algo-1", rule.Id);
        Assert.Equal("north", rule.RuleName);
        Assert.False(rule.IsActive);
        Assert.False(rule.MatchAll);
        Assert.Equal(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), rule.CreationTime);
        Assert.Equal(new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), rule.UpdateTime);
        Assert.Equal(RuleConverter.MigrationActor, rule.CreatedBy);
        Assert.Equal(RuleConverter.MigrationActor, rule.UpdatedBy);

        var match = rule.Match!;
        Assert.Collection(
            match.Sensors!,
            first =>
            {
                Assert.Equal("S1", first.Name);
                Assert.Equal(["grid-a", "grid-b"], first.GridTypes);
            },
            second =>
            {
                Assert.Equal("S2", second.Name);
                Assert.Empty(second.GridTypes);
            });
        Assert.All(match.Sensors!, sensor => Assert.Empty(sensor.RegistrationQualities));
        Assert.Equal(["SAR", "EO"], match.SensorTypes);
        Assert.Equal((0.3, 2.0), (match.Resolution!.Min, match.Resolution.Max));
        Assert.Equal("POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))", match.LocationWkt);
        Assert.Null(match.Location);
        Assert.Equal(MaxPhotoAgeDays, match.MaxPhotoAgeDays);
        using var expected = JsonDocument.Parse(
            """{"customer":"customer-a","modelName":"profile-a","hebrewRuleName":"משימה","algorithmName":"algo-x","priority":3,"username":"operator","runEveryOtherImage":false,"shouldCheckInVip":false}""");
        Assert.True(JsonElement.DeepEquals(expected.RootElement, Assert.Single(rule.RunParams!)));
    }

    [Fact]
    public void EmptyOrMissingConditionsAreOmitted()
    {
        var source = Algo();
        source.SensorNames = [];
        source.SensorTypes = [];
        source.MinimumResolution = null;
        source.MaximumResolution = null;
        source.IsPhotoOld = false;

        var match = AlgoRuleConverter.Convert(source, MaxPhotoAgeDays).Match!;

        Assert.Null(match.Sensors);
        Assert.Null(match.SensorTypes);
        Assert.Null(match.Resolution);
        Assert.Null(match.MaxPhotoAgeDays);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void MinimumResolutionOfZeroOrLessIsNoLowerBound(double minimum)
    {
        var source = Algo();
        source.MinimumResolution = minimum;

        var resolution = AlgoRuleConverter.Convert(source, MaxPhotoAgeDays).Match!.Resolution!;

        Assert.Null(resolution.Min);
        Assert.Equal(2, resolution.Max);
    }

    [Fact]
    public void ConvertedRulePassesDeepWisdomValidation()
    {
        var rule = AlgoRuleConverter.Convert(Algo(), MaxPhotoAgeDays);

        Assert.Empty(PipelineRuleValidator.Validate(rule, new DeepWisdomPipelineContract()).Errors);
    }

    [Fact]
    public void RuleWithoutLocationFailsDeepWisdomValidation()
    {
        var source = Algo();
        source.LocationWkt = " ";

        var rule = AlgoRuleConverter.Convert(source, MaxPhotoAgeDays);

        Assert.Null(rule.Match!.LocationWkt);
        Assert.Contains(
            PipelineRuleValidator.Validate(rule, new DeepWisdomPipelineContract()).Errors,
            error => error.StartsWith("match.locationWkt", StringComparison.Ordinal));
    }

    private static AlgoRuleDocument Algo()
    {
        var rule = JsonSerializer.Deserialize<AlgoRuleDocument>(
            """
            {
              "rule_name": "north", "hebrew_rule_name": "משימה", "algorithm_name": "algo-x",
              "customer": "customer-a", "profile_name": "profile-a", "username": "operator", "priority": 3,
              "is_active": false, "is_photo_old": true,
              "location_wkt": "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))",
              "location_geojson": { "type": "Polygon", "coordinates": [] },
              "minimum_resolution": 0.3, "maximum_resolution": 2,
              "sensor_types": ["SAR", "EO"],
              "sensor_names": { "S1": ["grid-a", "grid-b"], "S2": [] },
              "creation_time": "2026-01-01T00:00:00Z", "update_time": "2026-01-02T00:00:00Z"
            }
            """)!;
        rule.Id = "algo-1";
        return rule;
    }
}
