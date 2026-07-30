using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;

namespace ImagingPipeline.Gateway.Tests;

public sealed class RegistrationQualityContractTests
{
    [Fact]
    public void EveryDefinedValueHasExactJsonRoundTripAndUniqueMask()
    {
        RegistrationQualityContract.EnsureValid();
        var qualities = Enum.GetValues<RegistrationQuality>();

        var masks = qualities.Select(RegistrationQualityMask.From).ToArray();

        Assert.Equal(qualities.Length, RegistrationQualityContract.Count);
        Assert.Equal(masks.Length, masks.Distinct().Count());
        Assert.DoesNotContain(0, masks);

        foreach (var quality in qualities)
        {
            var name = Enum.GetName(quality);

            Assert.NotNull(name);
            Assert.True(RegistrationQualityExtensions.TryParseJsonValue(name, out var parsed));
            Assert.Equal(quality, parsed);
            Assert.Equal(name, quality.ToJsonValue());
            Assert.Equal($"\"{name}\"", JsonSerializer.Serialize(quality));
            Assert.Equal(quality, JsonSerializer.Deserialize<RegistrationQuality>($"\"{name}\""));
            Assert.Equal(1 << (int)quality, RegistrationQualityMask.From(quality));
            Assert.Contains(
                $"'{name}'",
                RegistrationQualityContract.AllowedJsonValues,
                StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("accurate")]
    [InlineData("sensor")]
    [InlineData("0")]
    [InlineData("")]
    public void ParserRejectsValuesThatAreNotExactEnumNames(string value)
    {
        Assert.False(RegistrationQualityExtensions.TryParseJsonValue(value, out _));
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<RegistrationQuality>($"\"{value}\""));
    }

    [Fact]
    public void JsonConverterRejectsNumericAndUndefinedValues()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<RegistrationQuality>("0"));

        var undefined = (RegistrationQuality)RegistrationQualityContract.Count;

        Assert.Throws<ArgumentOutOfRangeException>(() => undefined.ToJsonValue());
        Assert.Throws<ArgumentOutOfRangeException>(() => RegistrationQualityMask.From(undefined));
    }

    [Fact]
    public void GatewayParserUsesCachedAllowedValuesInValidationError()
    {
        var parser = new GatewayInputMessageParser(new GatewayGeometryConverter());
        var body = Encoding.UTF8.GetBytes(
            """
            {
              "overlay": {
                "id": "image-1",
                "sensorName": "camera",
                "sensorType": "EO",
                "registrationQuality": "accurate",
                "bestResolution": 1,
                "areaOfInterest": "region-alpha",
                "imageUrl": "/images/image-1.tiff",
                "width": 100,
                "height": 100,
                "photoTime": "2026-07-27T10:00:00Z",
                "roiFootprint": {
                  "type": "Polygon",
                  "coordinates": [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]]
                }
              }
            }
            """);

        var exception = Assert.Throws<GatewayValidationException>(() => parser.Parse(body));

        Assert.Equal("gateway.invalid_registration_quality", exception.ErrorCode);
        Assert.Equal(
            $"Input registration quality must be either {RegistrationQualityContract.AllowedJsonValues}.",
            exception.Message);
    }
}
