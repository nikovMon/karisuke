using System.Text.Json;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Errors;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayInputMessageParser
{
    private const string AccurateRegistrationQuality = "accurate";
    private const string SensorRegistrationQuality = "sensor";

    private readonly JsonPathReader _json;
    private readonly GatewayGeometryConverter _geometry;

    public GatewayInputMessageParser(
        JsonPathReader json,
        GatewayGeometryConverter geometry)
    {
        _json = json;
        _geometry = geometry;
    }

    public GatewayInputMessage Parse(ReadOnlyMemory<byte> body)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new GatewayValidationException($"Input body is not valid UTF-8 JSON: {ex.Message}", "gateway.invalid_json");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new GatewayValidationException("Input JSON top-level value must be an object.", "gateway.invalid_json_shape");
        }

        if (!_json.TryReadNonEmptyString(root, GatewayInputMessageSchema.ImageId, out var imageId))
        {
            throw new GatewayValidationException("Input image id is required and must be a non-empty string.", "gateway.missing_image_id");
        }

        if (!_json.TryReadNonEmptyString(root, GatewayInputMessageSchema.SensorName, out var sensorName))
        {
            throw new GatewayValidationException("Input sensor name is required and must be a non-empty string.", "gateway.missing_sensor_name");
        }

        if (!_json.TryReadNonEmptyString(root, GatewayInputMessageSchema.RegistrationQuality, out var registrationQuality))
        {
            throw new GatewayValidationException(
                "Input registration quality is required and must be a non-empty string.",
                "gateway.missing_registration_quality");
        }

        if (registrationQuality is not AccurateRegistrationQuality and not SensorRegistrationQuality)
        {
            throw new GatewayValidationException(
                "Input registration quality must be either 'accurate' or 'sensor'.",
                "gateway.invalid_registration_quality");
        }

        if (!_json.TryReadPositiveDouble(root, GatewayInputMessageSchema.Resolution, out var resolution))
        {
            throw new GatewayValidationException("Input resolution is required and must be a positive number.", "gateway.invalid_resolution");
        }

        var photoTime = ReadPhotoTime(root);
        var geometry = ReadGeometry(root);

        return new GatewayInputMessage(
            imageId,
            sensorName,
            registrationQuality,
            resolution,
            photoTime,
            geometry);
    }

    private DateTimeOffset? ReadPhotoTime(JsonElement root)
    {
        if (!_json.TryRead(root, GatewayInputMessageSchema.PhotoTime, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(element.GetString(), out var photoTime))
        {
            throw new GatewayValidationException(
                "Input photo time must be parseable as DateTimeOffset when provided.",
                "gateway.invalid_photo_time");
        }

        return photoTime.ToUniversalTime();
    }

    private Geometry ReadGeometry(JsonElement root)
    {
        if (_json.TryRead(root, GatewayInputMessageSchema.GeometryWkt, out var wktElement) &&
            wktElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(wktElement.GetString()))
        {
            return _geometry.ReadWkt(wktElement.GetString()!, "input");
        }

        if (_json.TryRead(root, GatewayInputMessageSchema.GeometryGeoJson, out var geoJsonElement) &&
            geoJsonElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return _geometry.ReadGeoJson(geoJsonElement, "input");
        }

        throw new GatewayValidationException(
            "Input geometry is required as WKT or GeoJSON.",
            "gateway.missing_geometry");
    }
}
