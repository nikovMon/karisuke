using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class RuleSnapshotBuilderTests
{
    [Fact]
    public void ValidRuleIsPreparedForMatching()
    {
        var rule = Rule();
        rule.Match!.MaxPhotoAgeDays = 30;
        rule.RunParams = [AsdRunParams("tenant-1"), AsdRunParams("tenant-2")];

        var snapshot = RuleSnapshotBuilder.Build(new([rule], []), Contract);

        var activeRule = Assert.Single(snapshot.Rules);
        Assert.Empty(snapshot.Rejections);
        Assert.Equal(TimeSpan.FromDays(30), activeRule.MaxPhotoAge);
        Assert.True(activeRule.Sensors.ContainsKey("camera"));
        Assert.False(activeRule.Geometry!.IsEmpty);
        Assert.Equal(2, activeRule.RunParams.Count);
        Assert.False(snapshot.AllRulesRejected);
    }

    [Fact]
    public void AbsentConditionsBecomeNoConstraint()
    {
        var rule = Rule();
        rule.Match = null;
        rule.MatchAll = true;

        var activeRule = Assert.Single(RuleSnapshotBuilder.Build(new([rule], []), Contract).Rules);

        Assert.Empty(activeRule.Sensors);
        Assert.Equal(0, activeRule.MinResolution);
        Assert.Equal(double.PositiveInfinity, activeRule.MaxResolution);
        Assert.Null(activeRule.MaxPhotoAge);
        Assert.Null(activeRule.Geometry);
    }

    [Fact]
    public void InvalidRulesAreRejectedWithAReasonWithoutStoppingTheOthers()
    {
        var invalidDocument = Rule("invalid-document");
        invalidDocument.Match = null;
        var invalidRunParams = Rule("invalid-run-params");
        invalidRunParams.RunParams = [AsdRunParams(" ")];
        var badGeometry = Rule("bad-geometry");
        badGeometry.Match!.LocationWkt = "POLYGON ((0 0";

        var snapshot = RuleSnapshotBuilder.Build(
            new([invalidDocument, Rule("valid"), invalidRunParams, badGeometry, null!], []), Contract);

        Assert.Equal("valid", Assert.Single(snapshot.Rules).Id);
        Assert.Collection(
            snapshot.Rejections,
            rejection =>
            {
                Assert.Equal("invalid-document", rejection.RuleId);
                Assert.Contains("matchAll", rejection.Reason, StringComparison.Ordinal);
            },
            rejection =>
            {
                Assert.Equal("invalid-run-params", rejection.RuleId);
                Assert.Contains("runParams[0].tenantId", rejection.Reason, StringComparison.Ordinal);
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
        var snapshot = RuleSnapshotBuilder.Build(new([], [new RuleRejection("unreadable", "Its source was null.")]), Contract);

        Assert.Empty(snapshot.Rules);
        Assert.Equal("unreadable", Assert.Single(snapshot.Rejections).RuleId);
        Assert.True(snapshot.AllRulesRejected);
    }

    [Fact]
    public void EmptyLoadIsNotFlagged()
    {
        Assert.False(RuleSnapshotBuilder.Build(new([], []), Contract).AllRulesRejected);
    }
}
