using System.Text;
using System.Text.Json.Nodes;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.RuleEngine.Input;

namespace ImagingPipeline.RuleEngine.Tests;

public sealed class GatewayInputMessageParserTests
{
    [Fact]
    public void ValidMessageIsParsed()
    {
        var input = GatewayInputMessageParser.Parse(Body());

        Assert.Equal("image-1", input.ImageId);
        Assert.Equal(RegistrationQuality.Accurate, input.RegistrationQuality);
        Assert.Equal("area", input.AreaOfInterest);
        Assert.Equal(TimeSpan.Zero, input.PhotoTime.Offset);
        Assert.False(input.Geometry.IsEmpty);
    }

    [Theory]
    [InlineData("id", "", "gateway.missing_image_id")]
    [InlineData("registrationQuality", "Unknown", "gateway.invalid_registration_quality")]
    [InlineData("bestResolution", 0, "gateway.invalid_best_resolution")]
    [InlineData("gridType", " ", "gateway.missing_grid_type")]
    public void InvalidFieldFailsWithItsErrorCode(string field, object value, string errorCode)
    {
        var body = Body(json => json[field] = JsonValue.Create(value));

        var exception = Assert.Throws<InvalidInputMessageException>(() => GatewayInputMessageParser.Parse(body));

        Assert.Equal(errorCode, exception.ErrorCode);
    }

    [Fact]
    public void MalformedJsonFailsAsInvalidJson()
    {
        var exception = Assert.Throws<InvalidInputMessageException>(
            () => GatewayInputMessageParser.Parse(Encoding.UTF8.GetBytes("{")));

        Assert.Equal("gateway.invalid_json", exception.ErrorCode);
    }

    [Fact]
    public void InvalidFootprintFailsAsInvalidGeometry()
    {
        var body = Body(json => json["roiFootprint"] = new JsonObject { ["type"] = "Polygon" });

        var exception = Assert.Throws<InvalidInputMessageException>(() => GatewayInputMessageParser.Parse(body));

        Assert.Equal("gateway.invalid_geometry", exception.ErrorCode);
    }

    private static byte[] Body(Action<JsonObject>? change = null)
    {
        var json = JsonNode.Parse("""
            {
              "id": "image-1",
              "sensorName": "camera",
              "sensorType": "EO",
              "registrationQuality": "Accurate",
              "bestResolution": 0.7,
              "areaOfInterest": " area ",
              "imageUrl": "https://example.invalid/image",
              "width": 100,
              "height": 100,
              "photoTime": "2026-10-01T15:00:00+03:00",
              "roiFootprint": { "type": "Polygon", "coordinates": [[[0, 0], [1, 0], [1, 1], [0, 1], [0, 0]]] },
              "gridType": "grid-a",
              "gridURI": "https://example.invalid/grid"
            }
            """)!.AsObject();
        change?.Invoke(json);
        return Encoding.UTF8.GetBytes(json.ToJsonString());
    }
}
