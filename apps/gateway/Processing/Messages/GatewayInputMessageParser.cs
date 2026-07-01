using System.Text.Json;
using ImagingPipeline.Gateway.Contracts.Messages;
using ImagingPipeline.Gateway.Errors;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayInputMessageParser
{
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

        if (!_json.TryReadOptionalString(root, GatewayInputMessageSchema.SensorType, out var sensorType))
        {
            throw new GatewayValidationException("Input sensor type must be a string when provided.", "gateway.invalid_sensor_type");
        }

        if (!_json.TryReadPositiveDouble(root, GatewayInputMessageSchema.Resolution, out var resolution))
        {
            throw new GatewayValidationException("Input resolution is required and must be a positive number.", "gateway.invalid_resolution");
        }

        var acquisitionTime = ReadAcquisitionTime(root);
        var geometry = ReadGeometry(root);

        return new GatewayInputMessage(
            imageId,
            sensorName,
            sensorType,
            resolution,
            acquisitionTime,
            geometry);
    }

    private DateTimeOffset? ReadAcquisitionTime(JsonElement root)
    {
        if (!_json.TryRead(root, GatewayInputMessageSchema.AcquisitionTime, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(element.GetString(), out var acquisitionTime))
        {
            throw new GatewayValidationException(
                "Input acquisition time must be parseable as DateTimeOffset when provided.",
                "gateway.invalid_acquisition_time");
        }

        return acquisitionTime.ToUniversalTime();
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
