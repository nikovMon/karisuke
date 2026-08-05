using System.Text.Json.Serialization;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class I2GByRegistrationRequestDto
{
    [JsonPropertyName("overlayId")]
    public required string OverlayId { get; init; }

    [JsonPropertyName("returnAltitude")]
    public bool ReturnAltitude { get; init; }

    [JsonPropertyName("pixelPoints")]
    public required IReadOnlyList<IReadOnlyList<double>> PixelPoints { get; init; }

    [JsonPropertyName("gridType")]
    public required string GridType { get; init; }

    [JsonPropertyName("gridURI")]
    public string? GridUri { get; init; }

    [JsonPropertyName("useCache")]
    public bool UseCache { get; init; }
}
