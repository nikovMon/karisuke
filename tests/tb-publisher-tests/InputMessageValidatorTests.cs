using System.Text;
using ImagingPipeline.TbPublisher.MessageHandling;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class InputMessageValidatorTests
{
    private readonly InputMessageValidator _validator = new();

    private static readonly string ValidBody = """
    {
      "taskId": "task-1",
      "ruleId": "rule-1",
      "algorithmName": ["FindAir", "Rpn"],
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
      "photoTime": "2026-07-27T10:00:00Z",
      "sensorType": "EO",
      "imageUrl": "/images/image-1.tiff",
      "imageWidth": 4096,
      "imageHeight": 3072,
      "bestResolution": 100,
      "sensorName": "sensor-1",
      "areaOfInterest": "region-alpha",
      "gridType": "EO",
      "gridURI": "grid://default",
      "tilingConfigs": [
        { "tileSizeWidth": 512, "tileSizeHeight": 512, "tileOverlapWidth": 32, "tileOverlapHeight": 32 }
      ]
    }
    """;

    [Fact]
    public void ValidateReturnsSuccessForWellFormedMessage()
    {
        var result = _validator.Validate(Encoding.UTF8.GetBytes(ValidBody));

        Assert.True(result.IsValid);
        Assert.NotNull(result.Message);
        Assert.Equal("rule-1", result.Message!.RuleId);
        Assert.Equal("image-1", result.Message.ImageId);
        Assert.Equal(2, result.Message.AlgorithmNames.Count);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidateAllowsMissingAreaMetadata()
    {
        var body = ValidBody.Replace(
            "\"areaOfInterest\": \"region-alpha\",",
            string.Empty,
            StringComparison.Ordinal);

        var result = _validator.Validate(Encoding.UTF8.GetBytes(body));

        Assert.True(result.IsValid);
        Assert.Null(result.Message!.AreaOfInterest);
    }
    [Fact]
    public void ValidateReturnsFailureForMalformedJson()
    {
        var body = Encoding.UTF8.GetBytes("{ not json");

        var result = _validator.Validate(body);

        Assert.False(result.IsValid);
        Assert.Null(result.Message);
        Assert.Contains(result.Errors, error => error.Contains("not valid JSON", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReturnsFailureForEmptyBody()
    {
        var body = Encoding.UTF8.GetBytes("null");

        var result = _validator.Validate(body);

        Assert.False(result.IsValid);
        Assert.Null(result.Message);
    }

    [Fact]
    public void ValidateReturnsFailureWhenRequiredFieldsAreMissing()
    {
        var body = Encoding.UTF8.GetBytes("{}");

        var result = _validator.Validate(body);

        Assert.False(result.IsValid);
        Assert.Contains(
            result.Errors,
            error => error.Contains("required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ValidateReturnsFailureWhenTilingConfigsAreInvalid()
    {
        var body = """
        {
          "taskId": "task-1",
          "ruleId": "rule-1",
          "algorithmName": ["FindAir"],
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
          "photoTime": "2026-07-27T10:00:00Z",
          "sensorType": "EO",
          "imageUrl": "/images/image-1.tiff",
          "imageWidth": 4096,
          "imageHeight": 3072,
          "bestResolution": 100,
          "sensorName": "sensor-1",
          "areaOfInterest": "region-alpha",
          "gridType": "EO",
          "gridURI": "grid://default",
          "tilingConfigs": [
            { "tileSizeWidth": 0, "tileSizeHeight": 512, "tileOverlapWidth": 0, "tileOverlapHeight": 0 }
          ]
        }
        """;

        var result = _validator.Validate(Encoding.UTF8.GetBytes(body));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Contains("tileSizeWidth", StringComparison.Ordinal));
    }
}
