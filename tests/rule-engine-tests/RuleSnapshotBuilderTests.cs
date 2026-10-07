using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class RuleSnapshotBuilderTests
{
    private readonly RuleSnapshotBuilder _builder = new(TimeSpan.FromDays(30));

    [Fact]
    public void ValidRulesArePreparedForMatching()
    {
        var rule = Rule();
        rule.IsPhotoOld = true;

        var snapshot = _builder.Build(new([rule], []));

        var activeRule = Assert.Single(snapshot.Rules);
        Assert.Empty(snapshot.Rejections);
        Assert.Equal(TimeSpan.FromDays(30), activeRule.MaxPhotoAge);
        Assert.True(activeRule.Sensors.ContainsKey("camera"));
        Assert.False(activeRule.Geometry.IsEmpty);
        Assert.False(snapshot.AllRulesRejected);
    }

    [Fact]
    public void InvalidRuleIsRejectedWithAReasonWithoutStoppingTheOthers()
    {
        var invalid = Rule("invalid");
        invalid.MinimumResolution = 0;
        var badGeometry = Rule("bad-geometry");
        badGeometry.LocationWkt = "POLYGON ((0 0";

        var snapshot = _builder.Build(new([invalid, Rule("valid"), badGeometry, null!], []));

        Assert.Equal("valid", Assert.Single(snapshot.Rules).Id);
        Assert.Collection(
            snapshot.Rejections,
            rejection =>
            {
                Assert.Equal("invalid", rejection.RuleId);
                Assert.Contains("minimumResolution", rejection.Reason, StringComparison.Ordinal);
            },
            rejection =>
            {
                Assert.Equal("bad-geometry", rejection.RuleId);
                Assert.NotNull(rejection.Exception);
            },
            rejection => Assert.Equal("<null>", rejection.RuleId));
    }

    [Fact]
    public void SourceRejectionsAreKeptAndAnAllRejectedLoadIsFlagged()
    {
        var snapshot = _builder.Build(new([], [new RuleRejection("unreadable", "Its source was null.")]));

        Assert.Empty(snapshot.Rules);
        Assert.Equal("unreadable", Assert.Single(snapshot.Rejections).RuleId);
        Assert.True(snapshot.AllRulesRejected);
    }

    [Fact]
    public void EmptyLoadIsNotFlagged()
    {
        Assert.False(_builder.Build(new([], [])).AllRulesRejected);
    }
}
