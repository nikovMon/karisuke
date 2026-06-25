using ImagingPipeline.Rules.Contracts.Models;
using ImagingPipeline.Rules.Contracts.Requests;
using ImagingPipeline.Rules.Api.Services;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleValidationTests
{
    [Fact]
    public void ValidateRuleReturnsAllRequiredFieldErrors()
    {
        var rule = new RuleConfigDto();

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Contains("_id cannot be empty", errors);
        Assert.Contains("ruleName cannot be empty", errors);
        Assert.Contains("algorithmName is required", errors);
        Assert.Contains("minResulution must be greater than 0", errors);
        Assert.Contains("RuleConfig must contain wkt, geoJson, or both", errors);
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
}
