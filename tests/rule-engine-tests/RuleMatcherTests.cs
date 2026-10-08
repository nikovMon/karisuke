using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.RuleEngine.Rules;
using Microsoft.Extensions.Time.Testing;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class RuleMatcherTests
{
    private readonly RuleMatcher _matcher = new(new FakeTimeProvider(Now));

    [Fact]
    public void MatchingImageReturnsTheRuleAndTheIntersection()
    {
        var evaluation = _matcher.Match(Image(), [ActiveRule()]);

        var match = Assert.Single(evaluation.Matches);
        Assert.Equal("rule-1", match.Rule.Id);
        Assert.Equal(1, match.IntersectionGeometry.Area, precision: 9);
        Assert.Empty(evaluation.Misses);
    }

    [Theory]
    [InlineData("other-camera", RegistrationQuality.Accurate, 0.7, "POLYGON ((1 1, 3 1, 3 3, 1 3, 1 1))", RuleMissReason.Sensor)]
    [InlineData("camera", RegistrationQuality.Sensor, 0.7, "POLYGON ((1 1, 3 1, 3 3, 1 3, 1 1))", RuleMissReason.Sensor)]
    [InlineData("camera", RegistrationQuality.Accurate, 1.5, "POLYGON ((1 1, 3 1, 3 3, 1 3, 1 1))", RuleMissReason.Resolution)]
    [InlineData("camera", RegistrationQuality.Accurate, 0.7, "POLYGON ((5 5, 6 5, 6 6, 5 6, 5 5))", RuleMissReason.Geometry)]
    public void FailingAConditionReportsIt(
        string sensor,
        RegistrationQuality quality,
        double resolution,
        string footprint,
        RuleMissReason reason)
    {
        var evaluation = _matcher.Match(Image(sensor, quality, resolution, footprintWkt: footprint), [ActiveRule()]);

        Assert.Empty(evaluation.Matches);
        Assert.Equal(new RuleMiss("rule-1", reason), Assert.Single(evaluation.Misses));
    }

    [Fact]
    public void OnlyTheFirstFailedConditionIsReported()
    {
        var evaluation = _matcher.Match(Image(sensorName: "other-camera", bestResolution: 5), [ActiveRule()]);

        Assert.Equal(RuleMissReason.Sensor, Assert.Single(evaluation.Misses).Reason);
    }

    [Fact]
    public void RuleWithoutSensorsMatchesAnySensor()
    {
        var rule = Rule();
        rule.Sensors = [];

        Assert.Single(_matcher.Match(Image(sensorName: "any-camera"), [ActiveRule(rule)]).Matches);
    }

    [Fact]
    public void SensorGridTypesRestrictTheMatch()
    {
        var rule = Rule();
        rule.Sensors = [new SensorConfig { Name = "camera", GridTypes = ["grid-a"] }];
        var activeRule = ActiveRule(rule);

        Assert.Single(_matcher.Match(Image(gridType: "grid-a"), [activeRule]).Matches);
        Assert.Empty(_matcher.Match(Image(gridType: "grid-b"), [activeRule]).Matches);
    }

    [Fact]
    public void PhotoOlderThanTheRuleLimitIsNoMatch()
    {
        var rule = Rule();
        rule.IsPhotoOld = true;
        var oldPhoto = Image(photoTime: Now.AddDays(-31));

        Assert.Equal(RuleMissReason.PhotoAge, Assert.Single(_matcher.Match(oldPhoto, [ActiveRule(rule)]).Misses).Reason);
        Assert.Single(_matcher.Match(oldPhoto, [ActiveRule()]).Matches);
    }
}
