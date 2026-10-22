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
        rule.Match!.Sensors = null;

        Assert.Single(_matcher.Match(Image(sensorName: "any-camera"), [ActiveRule(rule)]).Matches);
    }

    [Fact]
    public void SensorGridTypesRestrictTheMatch()
    {
        var rule = Rule();
        rule.Match!.Sensors = [new SensorConfig { Name = "camera", GridTypes = ["grid-a"] }];
        var activeRule = ActiveRule(rule);

        Assert.Single(_matcher.Match(Image(gridType: "grid-a"), [activeRule]).Matches);
        Assert.Empty(_matcher.Match(Image(gridType: "grid-b"), [activeRule]).Matches);
    }

    [Fact]
    public void PhotoOlderThanTheRuleLimitIsNoMatch()
    {
        var rule = Rule();
        rule.Match!.MaxPhotoAgeDays = 30;
        var oldPhoto = Image(photoTime: Now.AddDays(-31));

        Assert.Equal(RuleMissReason.PhotoAge, Assert.Single(_matcher.Match(oldPhoto, [ActiveRule(rule)]).Misses).Reason);
        Assert.Single(_matcher.Match(oldPhoto, [ActiveRule()]).Matches);
    }

    [Theory]
    [InlineData("A", true)]
    [InlineData("B", true)]
    [InlineData("C", false)]
    [InlineData("a", false)]
    [InlineData(null, false)]
    public void ModeSarMustBeOneOfTheRuleModes(string? imageMode, bool matches)
    {
        var rule = Rule();
        rule.Match!.ModeSar = ["A", "B"];

        var evaluation = _matcher.Match(Image(modeSar: imageMode), [ActiveRule(rule)]);

        Assert.Equal(matches, evaluation.Matches.Count == 1);
        if (!matches)
        {
            Assert.Equal(new RuleMiss("rule-1", RuleMissReason.ModeSar), Assert.Single(evaluation.Misses));
        }
    }

    [Theory]
    [InlineData("SAR", true)]
    [InlineData("EO", true)]
    [InlineData("IR", false)]
    [InlineData("sar", false)]
    [InlineData("", false)]
    public void SensorTypeMustBeOneOfTheRuleTypes(string imageType, bool matches)
    {
        var rule = Rule();
        rule.Match!.SensorTypes = ["SAR", "EO"];

        var evaluation = _matcher.Match(Image(sensorType: imageType), [ActiveRule(rule)]);

        Assert.Equal(matches, evaluation.Matches.Count == 1);
        if (!matches)
        {
            Assert.Equal(new RuleMiss("rule-1", RuleMissReason.SensorType), Assert.Single(evaluation.Misses));
        }
    }

    [Fact]
    public void SensorWithoutCriteriaAcceptsAnyQualityAndGridTypeForThatSensorOnly()
    {
        var rule = Rule();
        rule.Match!.Sensors = [new SensorConfig { Name = "camera" }];
        var active = ActiveRule(rule);

        Assert.Single(_matcher.Match(Image(quality: RegistrationQuality.Sensor, gridType: "any-grid"), [active]).Matches);
        Assert.Equal(RuleMissReason.Sensor, Assert.Single(_matcher.Match(Image(sensorName: "other"), [active]).Misses).Reason);
    }

    [Fact]
    public void RuleWithoutModeSarMatchesImagesWithOrWithoutOne()
    {
        Assert.Single(_matcher.Match(Image(modeSar: "A"), [ActiveRule()]).Matches);
        Assert.Single(_matcher.Match(Image(), [ActiveRule()]).Matches);
    }

    [Fact]
    public void RuleWithoutLocationCoversTheWholeImage()
    {
        var rule = Rule();
        rule.Match!.LocationWkt = null;
        var image = Image();

        var match = Assert.Single(_matcher.Match(image, [ActiveRule(rule)]).Matches);

        Assert.True(match.IntersectionGeometry.EqualsExact(image.Geometry));
    }

    [Fact]
    public void MatchAllRuleMatchesAnyImage()
    {
        var rule = Rule();
        rule.Match = null;
        rule.MatchAll = true;

        Assert.Single(_matcher.Match(Image(sensorName: "any", bestResolution: 99), [ActiveRule(rule)]).Matches);
    }
}
