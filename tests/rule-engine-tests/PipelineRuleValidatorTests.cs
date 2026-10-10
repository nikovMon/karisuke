using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Rules;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class PipelineRuleValidatorTests
{
    [Fact]
    public void ValidRuleHasNoErrorsAndItsGeometry()
    {
        var validation = PipelineRuleValidator.Validate(Rule(), Contract);

        Assert.True(validation.IsValid);
        Assert.False(validation.Geometry!.IsEmpty);
    }

    [Fact]
    public void RuleWithoutLocationHasNoGeometry()
    {
        var rule = Rule();
        rule.Match!.LocationWkt = null;

        var validation = PipelineRuleValidator.Validate(rule, Contract);

        Assert.True(validation.IsValid);
        Assert.Null(validation.Geometry);
    }

    [Fact]
    public void ErrorsFromEveryCheckAreReportedTogether()
    {
        var rule = Rule();
        rule.Match!.MaxPhotoAgeDays = 0;
        rule.Match.LocationWkt = "POLYGON ((0 0";
        rule.RunParams = [AsdRunParams(" ")];

        var errors = PipelineRuleValidator.Validate(rule, Contract).Errors;

        Assert.Contains(errors, error => error.Contains("maxPhotoAgeDays", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("runParams[0].tenantId", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.StartsWith("match.locationWkt", StringComparison.Ordinal));
    }

    [Fact]
    public void ContractLocationRequirementIsApplied()
    {
        var rule = Rule();
        rule.Match!.LocationWkt = null;

        var errors = PipelineRuleValidator.Validate(rule, new AlgoPipelineContract()).Errors;

        Assert.Contains(errors, error => error.StartsWith("match.locationWkt", StringComparison.Ordinal));
    }
}
