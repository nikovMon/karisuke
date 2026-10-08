using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using static ImagingPipeline.RuleEngine.Tests.RuleTestData;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class PipelineRuleDocumentValidationTests
{
    [Fact]
    public void ValidRuleHasNoErrors()
    {
        Assert.Empty(Errors(Rule()));
    }

    [Fact]
    public void RuleWithoutConditionsNeedsMatchAll()
    {
        var withoutMatch = Rule();
        withoutMatch.Match = null;
        var emptyMatch = Rule();
        emptyMatch.Match = new RuleMatchConditions();
        var matchAll = Rule();
        matchAll.Match = null;
        matchAll.MatchAll = true;

        Assert.Contains(Errors(withoutMatch), error => error.Contains("set matchAll to true", StringComparison.Ordinal));
        Assert.Contains(Errors(emptyMatch), error => error.Contains("set matchAll to true", StringComparison.Ordinal));
        Assert.Empty(Errors(matchAll));
    }

    [Fact]
    public void MatchAllCannotBeCombinedWithConditions()
    {
        var rule = Rule();
        rule.MatchAll = true;

        Assert.Contains("matchAll cannot be combined with match conditions", Errors(rule));
    }

    [Fact]
    public void EmptyCollectionsAreRejectedInsteadOfMeaningAny()
    {
        var emptySensors = Rule();
        emptySensors.Match!.Sensors = [];
        var emptyRunParams = Rule();
        emptyRunParams.RunParams = [];

        Assert.Contains("match.sensors cannot be empty; omit it to accept any sensor", Errors(emptySensors));
        Assert.Contains("runParams must contain at least one entry", Errors(emptyRunParams));
    }

    [Theory]
    [InlineData(null, null, "must set minimum, maximum or both")]
    [InlineData(0.0, null, "minimum must be a positive finite number")]
    [InlineData(2.0, 1.0, "maximum must be greater than or equal to minimum")]
    public void InvalidResolutionIsRejected(double? minimum, double? maximum, string error)
    {
        var rule = Rule();
        rule.Match!.Resolution = new ResolutionRange { Minimum = minimum, Maximum = maximum };

        Assert.Contains(Errors(rule), message => message.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void OneResolutionBoundIsEnough()
    {
        var rule = Rule();
        rule.Match!.Resolution = new ResolutionRange { Maximum = 1 };

        Assert.Empty(Errors(rule));
    }

    [Fact]
    public void OtherInvalidFieldsAreRejected()
    {
        var rule = Rule();
        rule.SchemaVersion = 1;
        rule.RuleName = " ";
        rule.Match!.LocationWkt = " ";
        rule.Match.MaxPhotoAgeDays = 0;
        rule.RunParams = [JsonSerializer.SerializeToElement("not an object")];

        Assert.Equal(
            [
                "schemaVersion must be 2",
                "ruleName cannot be empty",
                "match.locationWkt cannot be empty; omit it to accept any location",
                "match.maxPhotoAgeDays must be greater than 0",
                "runParams[0] must be an object"
            ],
            Errors(rule));
    }

    [Fact]
    public void DocumentRoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(Rule());
        var rule = JsonSerializer.Deserialize<PipelineRuleDocument>(json)!;

        Assert.Empty(Errors(rule));
        Assert.Equal(0.5, rule.Match!.Resolution!.Minimum);
        Assert.Contains("\"_id\":\"rule-1\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("maxPhotoAgeDays", json, StringComparison.Ordinal);
    }

    private static List<string> Errors(PipelineRuleDocument rule)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(rule, new ValidationContext(rule), results, validateAllProperties: true);
        return results.Select(result => result.ErrorMessage!).ToList();
    }
}
