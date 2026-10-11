using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.RuleEngine.Input;

/// <summary>
/// Parses and validates an incoming image update. Every field the matcher or a pipeline contract
/// relies on is checked here, so a bad message fails once with a stable error code.
/// </summary>
public static class GatewayInputMessageParser
{
    public static GatewayInputMessage Parse(ReadOnlyMemory<byte> body)
    {
        var input = Deserialize(body);

        Require(input.Id, "Input image id is required and must be a non-empty string.", "gateway.missing_image_id");
        Require(input.SensorName, "Input sensor name is required and must be a non-empty string.", "gateway.missing_sensor_name");
        Require(input.SensorType, "Input sensor type is required and must be a non-empty string.", "gateway.missing_sensor_type");
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

        Require(input.ImageUrl, "Input image URL is required and must be a non-empty string.", "gateway.missing_image_url");
        if (input.ImageWidth <= 0)
        {
            throw new InvalidInputMessageException(
                "Input image width is required and must be greater than zero.", "gateway.invalid_image_width");
        }

        if (input.ImageHeight <= 0)
        {
            throw new InvalidInputMessageException(
                "Input image height is required and must be greater than zero.", "gateway.invalid_image_height");
        }

        var geometry = ReadRoiFootprint(input.RoiFootprint);
        Require(input.GridType, "Input grid type is required and must be a non-empty string.", "gateway.missing_grid_type");
        Require(input.GridUri, "Input grid URI is required and must be a non-empty string.", "gateway.missing_grid_uri");

        return new GatewayInputMessage(
            input.Id,
            input.SensorName,
            input.SensorType,
            registrationQuality,
            input.BestResolution,
            string.IsNullOrWhiteSpace(input.AreaOfInterest) ? null : input.AreaOfInterest.Trim(),
            input.ImageUrl,
            input.ImageWidth,
            input.ImageHeight,
            input.PhotoTime.ToUniversalTime(),
            geometry,
            input.GridType,
            input.GridUri);
    }

    private static GatewayInputMessageDto Deserialize(ReadOnlyMemory<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize(body.Span, GatewayInputJsonSerializerContext.Default.GatewayInputMessageDto)
                ?? throw new InvalidInputMessageException("Input body cannot be JSON null.", "gateway.invalid_json_shape");
        }
        catch (JsonException ex)
        {
            throw new InvalidInputMessageException(
                $"Input body does not match the required JSON contract: {ex.Message}", "gateway.invalid_json");
        }
    }

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

[JsonSerializable(typeof(GatewayInputMessageDto))]
internal sealed partial class GatewayInputJsonSerializerContext : JsonSerializerContext;
