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
        var rule = new RuleDto
        {
            AlgorithmName = AlgorithmName.FindAir
        };

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Contains("ruleName cannot be empty", errors);
        Assert.Contains("minimumResolution must be greater than 0", errors);
        Assert.Contains("tenantsInfo must contain at least one tenant", errors);
        Assert.Contains("RuleConfig must contain locationWkt, locationGeoJson, or both", errors);
    }

    [Fact]
    public void ValidateRuleAcceptsGeoJsonWhenWktIsMissing()
    {
        var rule = ValidRule();
        rule.LocationWkt = null;
        rule.LocationGeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();

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
            MinimumResolution = 0
        };
        invalid.ProvidedFields.Add("ruleName");
        invalid.ProvidedFields.Add("algorithmName");
        invalid.ProvidedFields.Add("minimumResolution");

        var emptyErrors = RuleValidation.ValidateUpdate(empty);
        var invalidErrors = RuleValidation.ValidateUpdate(invalid);

        Assert.Equal(["At least one field must be provided."], emptyErrors);
        Assert.Contains("ruleName cannot be empty.", invalidErrors);
        Assert.Contains("algorithmName is required.", invalidErrors);
        Assert.Contains("minimumResolution must be greater than 0.", invalidErrors);
    }

    [Fact]
    public void ValidateUpdateRejectsNullActivityAndInvalidMaximumResolution()
    {
        var request = new UpdateRuleRequest
        {
            IsActive = null,
            MaximumResolution = null
        };
        request.ProvidedFields.Add("isActive");
        request.ProvidedFields.Add("maximumResolution");

        var errors = RuleValidation.ValidateUpdate(request);

        Assert.Contains("isActive cannot be null.", errors);
        Assert.Contains("maximumResolution must be greater than 0.", errors);
    }

    [Fact]
    public void RuleRequiresPositiveMaximumResolution()
    {
        var rule = new RuleDto
        {
            AlgorithmName = AlgorithmName.FindAir
        };

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Contains("maximumResolution must be greater than 0", errors);
        Assert.True(rule.IsActive);
    }

    [Fact]
    public void FullAndCreateModelsRequireAlgorithmNameDuringDeserialization()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDto>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CreateRuleRequest>("{}"));
    }

    [Fact]
    public void ValidateRuleRejectsInvalidTenantAndTilingConfiguration()
    {
        var rule = ValidRule();
        rule.TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = " ",
                TilingConfigs =
                [
                    new TilingConfig
                    {
                        TileSizeWidth = 0,
                        TileSizeHeight = -1,
                        TileOverlapWidth = -1
                    }
                ]
            }
        ];

        var errors = RuleValidation.ValidateRule(rule);

        Assert.Contains(errors, error => error.Contains("tenantId cannot be empty", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("tile dimensions must be greater than 0", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("tile overlaps cannot be negative", StringComparison.Ordinal));
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

    private static RuleDto ValidRule() =>
        new()
        {
            Id = "rule-1",
            RuleName = "one",
            AlgorithmName = AlgorithmName.FindAir,
            IsActive = true,
            MinimumResolution = 0.5,
            MaximumResolution = 1,
            Area = "area",
            LocationWkt = "POINT (1 1)",
            TenantsInfo = [ValidTenant()]
        };

    private static TenantInfo ValidTenant() =>
        new()
        {
            TenantId = "tenant-1",
            TilingConfigs =
            [
                new TilingConfig
                {
                    TileSizeWidth = 512,
                    TileSizeHeight = 512,
                    TileOverlapWidth = 32,
                    TileOverlapHeight = 32
                }
            ]
        };
}
