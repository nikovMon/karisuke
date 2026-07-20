using System.Text;
using ImagingPipeline.TbPublisher.MessageHandling;

namespace ImagingPipeline.TbPublisher.Tests;

public sealed class InputMessageValidatorTests
{
    private readonly InputMessageValidator _validator = new();

    private static readonly string ValidBody = """
    {
      "ruleId": "rule-1",
      "algorithmName": "FindAir",
      "tenantId": "tenant-1",
      "imageId": "image-1",
      "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
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
        Assert.Empty(result.Errors);
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
        Assert.Contains(result.Errors, error => error.Contains("ruleId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("tenantId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("imageId", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("roiFootprint", StringComparison.Ordinal));
        Assert.Contains(result.Errors, error => error.Contains("tilingConfigs", StringComparison.Ordinal));
    }

    [Fact]
    public void ValidateReturnsFailureWhenTilingConfigsAreInvalid()
    {
        var body = """
        {
          "ruleId": "rule-1",
          "algorithmName": "FindAir",
          "tenantId": "tenant-1",
          "imageId": "image-1",
          "roiFootprint": { "type": "Point", "coordinates": [35.98, 34.15] },
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
