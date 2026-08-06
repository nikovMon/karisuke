using System.Text.Json;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Errors;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayInputMessageParser
{
    private readonly GatewayGeometryConverter _geometry;

    public GatewayInputMessageParser(GatewayGeometryConverter geometry)
    {
        _geometry = geometry;
    }

    public GatewayInputMessage Parse(ReadOnlyMemory<byte> body)
    {
        GatewayInputMessageDto input;
        try
        {
            input = JsonSerializer.Deserialize(
                    body.Span,
                    GatewayInputJsonSerializerContext.Default.GatewayInputMessageDto)
                ?? throw new GatewayValidationException(
                    "Input body cannot be JSON null.",
                    "gateway.invalid_json_shape");
        }
        catch (JsonException ex)
        {
            throw new GatewayValidationException(
                $"Input body does not match the required JSON contract: {ex.Message}",
                "gateway.invalid_json");
        }

        var overlay = input.Overlay
            ?? throw new GatewayValidationException(
                "Input overlay is required.",
                "gateway.missing_overlay");
        if (string.IsNullOrWhiteSpace(overlay.Id))
        {
            throw new GatewayValidationException(
                "Input image id is required and must be a non-empty string.",
                "gateway.missing_image_id");
        }

        if (string.IsNullOrWhiteSpace(overlay.SensorName))
        {
            throw new GatewayValidationException(
                "Input sensor name is required and must be a non-empty string.",
                "gateway.missing_sensor_name");
        }

        if (string.IsNullOrWhiteSpace(overlay.SensorType))
        {
            throw new GatewayValidationException(
                "Input sensor type is required and must be a non-empty string.",
                "gateway.missing_sensor_type");
        }

        if (string.IsNullOrWhiteSpace(overlay.RegistrationQuality))
        {
            throw new GatewayValidationException(
                "Input registration quality is required and must be a non-empty string.",
                "gateway.missing_registration_quality");
        }

        if (!RegistrationQualityExtensions.TryParseJsonValue(
                overlay.RegistrationQuality,
                out var registrationQuality))
        {
            throw new GatewayValidationException(
                $"Input registration quality must be either {RegistrationQualityContract.AllowedJsonValues}.",
                "gateway.invalid_registration_quality");
        }

        ValidatePositiveFinite(
            overlay.BestResolution,
            "Input best resolution is required and must be a positive finite number.",
            "gateway.invalid_best_resolution");

        if (string.IsNullOrWhiteSpace(overlay.ImageUrl))
        {
            throw new GatewayValidationException(
                "Input image URL is required and must be a non-empty string.",
                "gateway.missing_image_url");
        }

        if (overlay.ImageWidth <= 0)
        {
            throw new GatewayValidationException(
                "Input image width is required and must be greater than zero.",
                "gateway.invalid_image_width");
        }

        if (overlay.ImageHeight <= 0)
        {
            throw new GatewayValidationException(
                "Input image height is required and must be greater than zero.",
                "gateway.invalid_image_height");
        }

        if (overlay.RoiFootprint.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            throw new GatewayValidationException(
                "Input roiFootprint is required.",
                "gateway.missing_geometry");
        }

        var geometry = _geometry.ReadGeoJson(overlay.RoiFootprint, "input roiFootprint");

        if (string.IsNullOrWhiteSpace(overlay.GridType))
        {
            throw new GatewayValidationException(
                "Input grid type is required and must be a non-empty string.",
                "gateway.missing_grid_type");
        }

        if (string.IsNullOrWhiteSpace(overlay.GridUri))
        {
            throw new GatewayValidationException(
                "Input grid URI is required and must be a non-empty string.",
                "gateway.missing_grid_uri");
        }

        return new GatewayInputMessage(
            overlay.Id,
            overlay.SensorName,
            overlay.SensorType,
            registrationQuality,
            overlay.BestResolution,
            overlay.AreaOfInterest,
            overlay.ImageUrl,
            overlay.ImageWidth,
            overlay.ImageHeight,
            overlay.PhotoTime.ToUniversalTime(),
            geometry,
            overlay.GridType,
            overlay.GridUri);
    }

    private static void ValidatePositiveFinite(
        double value,
        string message,
        string errorCode)
    {
        if (!double.IsFinite(value) || value <= 0)
        {
            throw new GatewayValidationException(message, errorCode);
        }
    }
}

[JsonSerializable(typeof(GatewayInputMessageDto))]
internal sealed partial class GatewayInputJsonSerializerContext : JsonSerializerContext
{
}
