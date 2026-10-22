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
    [InlineData(null, null, "must set min, max or both")]
    [InlineData(0.0, null, "min must be a positive finite number")]
    [InlineData(2.0, 1.0, "max must be greater than or equal to min")]
    public void InvalidResolutionIsRejected(double? min, double? max, string error)
    {
        var rule = Rule();
        rule.Match!.Resolution = new ResolutionRange { Min = min, Max = max };

        Assert.Contains(Errors(rule), message => message.Contains(error, StringComparison.Ordinal));
    }

    [Fact]
    public void OneResolutionBoundIsEnough()
    {
        var rule = Rule();
        rule.Match!.Resolution = new ResolutionRange { Max = 1 };

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
    public void ModeSarIsAConditionOnItsOwn()
    {
        var rule = Rule();
        rule.Match = new RuleMatchConditions { ModeSar = ["A", "B"] };

        Assert.Empty(Errors(rule));
    }

    [Theory]
    [InlineData(new string[0], "match.modeSAR cannot be empty; omit it to accept any mode")]
    [InlineData(new[] { "A", " " }, "match.modeSAR contains empty or whitespace values")]
    [InlineData(new[] { "A", "A" }, "match.modeSAR contains duplicate values")]
    public void InvalidModeSarIsRejected(string[] modes, string error)
    {
        var rule = Rule();
        rule.Match!.ModeSar = [.. modes];

        Assert.Contains(error, Errors(rule));
    }

    [Fact]
    public void SensorTypesIsAConditionOnItsOwn()
    {
        var rule = Rule();
        rule.Match = new RuleMatchConditions { SensorTypes = ["SAR", "EO"] };

        Assert.Empty(Errors(rule));
    }

    [Theory]
    [InlineData(new string[0], "match.sensorTypes cannot be empty; omit it to accept any sensor type")]
    [InlineData(new[] { "SAR", "" }, "match.sensorTypes contains empty or whitespace values")]
    [InlineData(new[] { "SAR", "SAR" }, "match.sensorTypes contains duplicate values")]
    public void InvalidSensorTypesAreRejected(string[] types, string error)
    {
        var rule = Rule();
        rule.Match!.SensorTypes = [.. types];

        Assert.Contains(error, Errors(rule));
    }

    [Fact]
    public void SensorNamesThatDifferOnlyInCaseAreDuplicates()
    {
        var rule = Rule();
        rule.Match!.Sensors = [new SensorConfig { Name = "camera" }, new SensorConfig { Name = "CAMERA" }];

        Assert.Contains("match.Duplicate sensor name 'CAMERA'", Errors(rule));
    }

    [Fact]
    public void SensorWithoutQualitiesOrGridTypesIsAllowed()
    {
        var rule = Rule();
        rule.Match!.Sensors = [new SensorConfig { Name = "camera" }];

        Assert.Empty(Errors(rule));
    }

    [Fact]
    public void ModeSarKeepsItsSourceSpellingInJson()
    {
        var rule = Rule();
        rule.Match!.ModeSar = ["A"];

        var json = JsonSerializer.Serialize(rule);

        Assert.Contains("\"modeSAR\":[\"A\"]", json, StringComparison.Ordinal);
        Assert.Equal(["A"], JsonSerializer.Deserialize<PipelineRuleDocument>(json)!.Match!.ModeSar);
    }

    [Fact]
    public void DocumentRoundTripsThroughJson()
    {
        var json = JsonSerializer.Serialize(Rule());
        var rule = JsonSerializer.Deserialize<PipelineRuleDocument>(json)!;

        Assert.Empty(Errors(rule));
        Assert.Equal(0.5, rule.Match!.Resolution!.Min);
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
