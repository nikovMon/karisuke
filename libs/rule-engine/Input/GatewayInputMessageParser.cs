using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Input;

/// <summary>
/// Validates the fields needed to identify an image and match rules. Pipeline-specific metadata
/// remains optional here; each selected contract validates the original values it consumes.
/// </summary>
public static class GatewayInputMessageParser
{
    public static GatewayInputMessage Parse(ReadOnlyMemory<byte> body)
    {
        var input = Deserialize(body);

        Require(input.Id, "Input image id is required and must be a non-empty string.", "gateway.missing_image_id");
        Require(input.SensorName, "Input sensor name is required and must be a non-empty string.", "gateway.missing_sensor_name");
        Require(
            input.RegistrationQuality,
            "Input registration quality is required and must be a non-empty string.",
            "gateway.missing_registration_quality");
        if (!RegistrationQualityExtensions.TryParseJsonValue(input.RegistrationQuality, out var registrationQuality))
        {
            throw new InvalidInputMessageException(
                $"Input registration quality must be either {RegistrationQualityContract.AllowedJsonValues}.",
                "gateway.invalid_registration_quality");
        }

        if (!double.IsFinite(input.BestResolution) || input.BestResolution <= 0)
        {
            throw new InvalidInputMessageException(
                "Input best resolution is required and must be a positive finite number.",
                "gateway.invalid_best_resolution");
        }

        var geometry = ReadRoiFootprint(input.RoiFootprint);
        Require(input.GridType, "Input grid type is required and must be a non-empty string.", "gateway.missing_grid_type");

        var area = OptionalString(input.AreaOfInterest);
        var modeSar = OptionalString(input.ModeSar);

        return new GatewayInputMessage(
            input.Id,
            input.SensorName,
            OptionalString(input.SensorType) ?? string.Empty,
            registrationQuality,
            input.BestResolution,
            string.IsNullOrWhiteSpace(area) ? null : area.Trim(),
            OptionalString(input.ImageUrl) ?? string.Empty,
            OptionalInteger(input.ImageWidth),
            OptionalInteger(input.ImageHeight),
            input.PhotoTime.ToUniversalTime(),
            geometry,
            input.GridType,
            OptionalString(input.GridUri) ?? string.Empty,
            string.IsNullOrWhiteSpace(modeSar) ? null : modeSar);
    }

    private static GatewayMatchInputDto Deserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize(body.Span, GatewayInputJsonSerializerContext.Default.GatewayMatchInputDto)
                ?? throw new InvalidInputMessageException("Input body cannot be JSON null.", "gateway.invalid_json_shape");
        }
        catch (JsonException ex)
        {
            throw new InvalidInputMessageException(
                $"Input body does not match the required JSON contract: {ex.Message}", "gateway.invalid_json");
        }
    }

    // These convenience values are only used for matching/telemetry compatibility. Contract
    // projection reads the original body and preserves type errors for the affected pipeline.
    private static string? OptionalString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int OptionalInteger(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) ? number : 0;

    private static Geometry ReadRoiFootprint(JsonElement roiFootprint)
    {
        if (roiFootprint.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new InvalidInputMessageException("Input roiFootprint is required.", "gateway.missing_geometry");
        }

        try
        {
            return GeometryUtilities.ReadGeoJson(roiFootprint);
        }
        catch (Exception ex)
        {
            throw new InvalidInputMessageException(
                $"input roiFootprint GeoJSON geometry is invalid: {ex.Message}", "gateway.invalid_geometry");
        }
    }

    private static void Require(string? value, string message, string errorCode)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidInputMessageException(message, errorCode);
        }
    }
}

[JsonSerializable(typeof(GatewayMatchInputDto))]
internal sealed partial class GatewayInputJsonSerializerContext : JsonSerializerContext;

// The matcher validates its required fields here. JsonElement defers pipeline-specific
// type validation until a selected contract needs those values.
internal sealed class GatewayMatchInputDto
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("sensorName")]
    public required string SensorName { get; init; }

    [JsonPropertyName("registrationQuality")]
    public required string RegistrationQuality { get; init; }

    [JsonPropertyName("bestResolution")]
    public required double BestResolution { get; init; }

    [JsonPropertyName("photoTime")]
    public required DateTimeOffset PhotoTime { get; init; }

    [JsonPropertyName("roiFootprint")]
    public required JsonElement RoiFootprint { get; init; }

    [JsonPropertyName("gridType")]
    public required string GridType { get; init; }

    [JsonPropertyName("sensorType")]
    public JsonElement SensorType { get; init; }

    [JsonPropertyName("areaOfInterest")]
    public JsonElement AreaOfInterest { get; init; }

    [JsonPropertyName("imageUrl")]
    public JsonElement ImageUrl { get; init; }

    [JsonPropertyName("width")]
    public JsonElement ImageWidth { get; init; }

    [JsonPropertyName("height")]
    public JsonElement ImageHeight { get; init; }

    [JsonPropertyName("gridURI")]
    public JsonElement GridUri { get; init; }

    [JsonPropertyName("modeSAR")]
    public JsonElement ModeSar { get; init; }
}
