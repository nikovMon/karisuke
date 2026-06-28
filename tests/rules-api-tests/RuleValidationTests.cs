using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Rules.Api.Services;
using System.Text.Json;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleValidationTests
{
    [Fact]
    public void ValidateRuleReturnsAllRequiredFieldErrors()
    {
        var rule = new RuleConfigDto();

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Contains("ruleName cannot be empty", errors);
        Assert.Contains("algorithmName is required", errors);
        Assert.Contains("minResolution must be greater than 0", errors);
        Assert.Contains("RuleConfig must contain wkt, geoJson, or both", errors);
    }

    [Fact]
    public void ValidateRuleAcceptsGeoJsonWhenWktIsMissing()
    {
        var rule = ValidRule();
        rule.Wkt = null;
        rule.GeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateUpdateReturnsAllRelevantErrors()
    {
        var empty = new UpdateRuleRequest();
        var invalid = new UpdateRuleRequest
        {
            RuleName = " ",
            AlgorithmName = null,
            MinResolution = 0
        };
        invalid.ProvidedFields.Add("ruleName");
        invalid.ProvidedFields.Add("algorithmName");
        invalid.ProvidedFields.Add("minResolution");

        var emptyErrors = RuleValidation.ValidateUpdate(empty);
        var invalidErrors = RuleValidation.ValidateUpdate(invalid);

        Assert.Equal(["At least one field must be provided."], emptyErrors);
        Assert.Contains("ruleName cannot be empty.", invalidErrors);
        Assert.Contains("algorithmName is required.", invalidErrors);
        Assert.Contains("minResolution must be greater than 0.", invalidErrors);
    }

    [Fact]
    public void ValidateUpdateRejectsNullActivityAndInvalidMaximumResolution()
    {
        var request = new UpdateRuleRequest
        {
            IsActive = null,
            MaxResolution = null
        };
        request.ProvidedFields.Add("isActive");
        request.ProvidedFields.Add("maxResolution");

        var errors = RuleValidation.ValidateUpdate(request);

        Assert.Contains("isActive cannot be null.", errors);
        Assert.Contains("maxResolution must be greater than 0.", errors);
    }

    [Fact]
    public void RuleDefaultsMaximumResolutionTo999()
    {
        var rule = new RuleConfigDto();

        Assert.Equal(999, rule.MaxResolution);
    }

    [Fact]
    public void ValidateIdsRejectsEmptyOrWhitespaceIds()
    {
        var emptyErrors = RuleValidation.ValidateIds([]);
        var whitespaceErrors = RuleValidation.ValidateIds(["rule-1", " "]);

        Assert.Equal(["ids list cannot be empty."], emptyErrors);
        Assert.Equal(["ids list cannot contain empty values."], whitespaceErrors);
    }

    [Fact]
    public void ValidateSensorRequestRejectsEmptyAndDuplicateValues()
    {
        var request = new RuleSensorUpdateRequest
        {
            SensorName = "",
            Values = ["cam-1", "cam-1", ""]
        };

        var errors = RuleValidation.ValidateSensorRequest(request);

        Assert.Contains("sensorName cannot be empty.", errors);
        Assert.Contains("sensor values cannot be empty.", errors);
        Assert.Contains("sensor values must be unique.", errors);
    }

    [Fact]
    public void SensorValidationRejectsNullCollectionsWithoutThrowing()
    {
        var rule = ValidRule();
        rule.Sensors = new Dictionary<string, List<string>>(StringComparer.Ordinal)
        {
            ["camera"] = null!
        };
        var update = new UpdateRuleRequest
        {
            Sensors = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["camera"] = null!
            }
        };
        update.ProvidedFields.Add("sensors");

        var ruleErrors = RuleValidation.ValidateRule(rule);
        var updateErrors = RuleValidation.ValidateUpdate(update);
        var requestErrors = RuleValidation.ValidateSensorRequest(new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            Values = null!
        });

        Assert.Contains(ruleErrors, error => error.Contains("cannot be null", StringComparison.Ordinal));
        Assert.Contains("sensor value lists cannot be null.", updateErrors);
        Assert.Contains("sensor values cannot be empty.", requestErrors);
    }

    [Fact]
    public async Task UpdateReaderTracksOnlySentFieldsIncludingNulls()
    {
        await using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"description\":null,\"isActive\":false}"));

        var request = await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateRuleRequest>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.True(request.HasField("description"));
        Assert.True(request.HasField("isActive"));
        Assert.False(request.HasField("ruleName"));
        Assert.Null(request.Description);
        Assert.False(request.IsActive.GetValueOrDefault(true));
    }

    [Fact]
    public async Task UpdateReaderIgnoresUnknownFieldsAndTracksKnownValues()
    {
        await using var body = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(
            "{\"unknown\":\"value\",\"ruleName\":\"new-name\"}"));

        var request = await System.Text.Json.JsonSerializer.DeserializeAsync<UpdateRuleRequest>(
            body,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.False(request.HasField("unknown"));
        Assert.True(request.HasField("ruleName"));
        Assert.Equal("new-name", request.RuleName);
        Assert.False(request.HasField("description"));
    }

    private static RuleConfigDto ValidRule() =>
        new()
        {
            Id = "rule-1",
            RuleName = "one",
            AlgorithmName = AlgorithmName.Finder,
            IsActive = true,
            MinResolution = 0.5,
            MaxResolution = 1,
            Area = "area",
            Wkt = "POINT (1 1)"
        };
}
