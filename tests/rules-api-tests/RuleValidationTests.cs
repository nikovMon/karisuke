using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Rules.Api.Services;
using static ImagingPipeline.Common.Dtos.Rules.Models.RegistrationQuality;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RuleValidationTests
{
    [Fact]
    public void RegistrationQualityJsonUsesPascalCaseValuesAndRejectsLowercase()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var request = new RuleSensorUpdateRequest
        {
            SensorName = "camera",
            RegistrationQualities = [Accurate, Sensor]
        };

        var parsed = JsonSerializer.Deserialize<RuleSensorUpdateRequest>(
            """{"sensorName":"camera","registrationQualities":["Accurate","Sensor"]}""",
            options);
        var json = JsonSerializer.Serialize(request, options);

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleSensorUpdateRequest>(
            """{"sensorName":"camera","registrationQualities":["accurate","sensor"]}""",
            options));
        Assert.Contains("\"registrationQualities\":[\"Accurate\",\"Sensor\"]", json, StringComparison.Ordinal);
        Assert.Equal([Accurate, Sensor], parsed?.RegistrationQualities);
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
        var duplicateRequest = new RuleSensorUpdateRequest
        {
            SensorName = "",
            RegistrationQualities = [Accurate, Accurate]
        };
        var noValuesRequest = new RuleSensorUpdateRequest
        {
            SensorName = "camera"
        };

        var duplicateErrors = RuleValidation.ValidateSensorRequest(duplicateRequest);
        var noValueErrors = RuleValidation.ValidateSensorRequest(noValuesRequest);

        Assert.Contains("sensorName cannot be empty.", duplicateErrors);
        Assert.Contains("registrationQualities values must be unique.", duplicateErrors);
        Assert.Contains("At least one of registrationQualities or gridTypes must be provided with values.", noValueErrors);
    }

    [Fact]
    public void ValidateSensorRequestRejectsEmptyListsAndBadGridTypes()
    {
        var emptyLists = new RuleSensorUpdateRequest { SensorName = "camera", RegistrationQualities = [], GridTypes = [] };
        var badGridTypes = new RuleSensorUpdateRequest { SensorName = "camera", GridTypes = ["grid-a", "grid-a", " "] };

        var emptyListErrors = RuleValidation.ValidateSensorRequest(emptyLists);
        var gridTypeErrors = RuleValidation.ValidateSensorRequest(badGridTypes);

        Assert.Contains("At least one of registrationQualities or gridTypes must be provided with values.", emptyListErrors);
        Assert.Contains("gridTypes values must be unique.", gridTypeErrors);
        Assert.Contains("gridTypes values must be non-empty strings.", gridTypeErrors);
    }
}
