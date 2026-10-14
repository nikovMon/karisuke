using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using static ImagingPipeline.Common.Dtos.Rules.Models.RegistrationQuality;

namespace ImagingPipeline.Gateway.Tests;

/// <summary>
/// Validation of the v1 rule document the gateway loads from the shared <c>rules</c> index.
/// </summary>
public sealed class RuleDtoValidationTests
{
    [Fact]
    public void MissingRequiredFieldsAreAllReported()
    {
        var errors = Validate(new RuleDto { AlgorithmNames = [AlgorithmName.FindAir] });

        Assert.Contains("ruleName cannot be empty", errors);
        Assert.Contains("minimumResolution must be greater than 0", errors);
        Assert.Contains("maximumResolution must be greater than 0", errors);
        Assert.Contains("tenantsInfo must contain at least one tenant", errors);
        Assert.Contains("locationWkt is required", errors);
    }

    [Fact]
    public void GeoJsonWithoutWktIsRejected()
    {
        var rule = ValidRule();
        rule.LocationWkt = string.Empty;
        rule.LocationGeoJson = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}").RootElement.Clone();

        Assert.Contains("locationWkt is required", Validate(rule));
    }

    [Fact]
    public void AlgorithmNameIsARequiredArrayOfExactUniqueValues()
    {
        var both = JsonSerializer.Deserialize<RuleDto>("""{"algorithmName":["FindAir","Rpn"]}""");
        var duplicate = ValidRule();
        duplicate.AlgorithmNames = [AlgorithmName.FindAir, AlgorithmName.FindAir];

        Assert.Equal([AlgorithmName.FindAir, AlgorithmName.Rpn], both?.AlgorithmNames);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDto>("{}"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDto>("""{"algorithmName":"FindAir"}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDto>("""{"algorithmName":["findair"]}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<RuleDto>("""{"algorithmName":[0]}"""));
        Assert.Contains(Validate(duplicate), error => error.Contains("algorithmName values must be unique", StringComparison.Ordinal));
    }

    [Fact]
    public void InvalidTenantAndTilingConfigurationIsRejected()
    {
        var rule = ValidRule();
        rule.TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = " ",
                TilingConfigs = [new TilingConfig { TileSizeWidth = 0, TileSizeHeight = -1, TileOverlapWidth = -1 }]
            }
        ];

        var errors = Validate(rule);

        Assert.Contains(errors, error => error.Contains("tenantId cannot be empty", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("tile dimensions must be greater than 0", StringComparison.Ordinal));
        Assert.Contains(errors, error => error.Contains("tile overlaps cannot be negative", StringComparison.Ordinal));
    }

    [Fact]
    public void NullOrEmptySensorEntriesAreRejectedWithoutThrowing()
    {
        var nullEntry = ValidRule();
        nullEntry.Sensors = [null!];
        var emptyEntry = ValidRule();
        emptyEntry.Sensors = [new SensorConfig { Name = "camera" }];

        Assert.Contains(Validate(nullEntry), error => error.Contains("cannot be null", StringComparison.Ordinal));
        Assert.Contains(Validate(emptyEntry), error => error.Contains("at least one registrationQuality or gridType", StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<string> Validate(RuleDto rule)
    {
        var results = new List<ValidationResult>();
        Validator.TryValidateObject(rule, new ValidationContext(rule), results, validateAllProperties: true);
        return results.Select(result => result.ErrorMessage ?? string.Empty).ToArray();
    }

    private static RuleDto ValidRule() => new()
    {
        Id = "rule-1",
        RuleName = "one",
        AlgorithmNames = [AlgorithmName.FindAir],
        MinimumResolution = 0.5,
        MaximumResolution = 1,
        Area = "area",
        LocationWkt = "POINT (1 1)",
        Sensors = [new SensorConfig { Name = "camera", RegistrationQualities = [Accurate] }],
        TenantsInfo =
        [
            new TenantInfo
            {
                TenantId = "tenant-1",
                TilingConfigs = [new TilingConfig { TileSizeWidth = 512, TileSizeHeight = 512, TileOverlapWidth = 32, TileOverlapHeight = 32 }]
            }
        ]
    };
}
