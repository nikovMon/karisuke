using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Common.Dtos.Gateway.Messages;

public sealed class GatewayInputMessageDto
{
    [JsonPropertyName("overlay")]
    public required GatewayInputOverlayDto Overlay { get; init; }
}

public sealed class GatewayInputOverlayDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sensorName")]
    public required string SensorName { get; init; }

    [JsonPropertyName("sensorType")]
    public required string SensorType { get; init; }

    [JsonPropertyName("registrationQuality")]
    public required string RegistrationQuality { get; init; }

    [JsonPropertyName("bestResolution")]
    public required double BestResolution { get; init; }

    [JsonPropertyName("areaOfInterest")]
    public required string AreaOfInterest { get; init; }

    [JsonPropertyName("imageUrl")]
    public required string ImageUrl { get; init; }

    [JsonPropertyName("width")]
    public required int ImageWidth { get; init; }

    [JsonPropertyName("height")]
    public required int ImageHeight { get; init; }

    [JsonPropertyName("photoTime")]
    public required DateTimeOffset PhotoTime { get; init; }

    [JsonPropertyName("roiFootprint")]
    public required JsonElement RoiFootprint { get; init; }

    [JsonPropertyName("gridType")]
    public required string GridType { get; init; }

    [JsonPropertyName("gridURI")]
    public string? GridUri { get; init; }
}
