using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Domain;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Application.Messages;

public sealed class InputMessageValidator
{
    private readonly JsonPathReader _pathReader;
    private readonly GeometryExtractor _geometryExtractor;
    private readonly InputFieldPathSettings _paths;

    public InputMessageValidator(
        JsonPathReader pathReader,
        GeometryExtractor geometryExtractor,
        IOptions<InputFieldPathSettings> paths)
    {
        _pathReader = pathReader;
        _geometryExtractor = geometryExtractor;
        _paths = paths.Value;
    }

    public ValidatedInputMessage Validate(
        ReadOnlyMemory<byte> body,
        IReadOnlyList<RuleConfigDto> activeRules)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(body);
            root = document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new NonRetryableGatewayException($"Input body is not valid UTF-8 JSON: {ex.Message}", "gateway.invalid_json");
        }

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new NonRetryableGatewayException("Input JSON top-level value must be an object.", "gateway.invalid_json_shape");
        }

        if (!_pathReader.TryReadNonEmptyString(root, _paths.ImageIdPath, out var imageId))
        {
            throw new NonRetryableGatewayException("Input image id is required and must be a non-empty string.", "gateway.missing_image_id");
        }

        if (!_pathReader.TryReadNonEmptyString(root, _paths.SensorNamePath, out var sensorName))
        {
            throw new NonRetryableGatewayException("Input sensor name is required and must be a non-empty string.", "gateway.missing_sensor_name");
        }

        if (!_pathReader.TryReadOptionalString(root, _paths.SensorTypePath, out var sensorType))
        {
            throw new NonRetryableGatewayException("Input sensor type must be a string when provided.", "gateway.invalid_sensor_type");
        }

        if (!_pathReader.TryReadPositiveDouble(root, _paths.ResolutionPath, out var resolution))
        {
            throw new NonRetryableGatewayException("Input resolution is required and must be a positive number.", "gateway.invalid_resolution");
        }

        var acquisitionTime = ReadAcquisitionTime(root, activeRules);
        var geometry = ReadGeometry(root);

        return new ValidatedInputMessage(
            root,
            imageId,
            sensorName,
            sensorType,
            resolution,
            acquisitionTime,
            geometry);
    }

    private DateTimeOffset? ReadAcquisitionTime(
        JsonElement root,
        IReadOnlyList<RuleConfigDto> activeRules)
    {
        var hasLookbackRule = activeRules.Any(rule => rule.MaxLookBackDay.HasValue);
        if (!_pathReader.TryRead(root, _paths.AcquisitionTimePath, out var element) ||
            element.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            if (hasLookbackRule)
            {
                throw new NonRetryableGatewayException(
                    "Input acquisition time is required because at least one active rule has maxLookBackDay.",
                    "gateway.missing_acquisition_time");
            }

            return null;
        }

        if (element.ValueKind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(element.GetString(), out var acquisitionTime))
        {
            throw new NonRetryableGatewayException(
                "Input acquisition time must be parseable as DateTimeOffset when provided.",
                "gateway.invalid_acquisition_time");
        }

        return acquisitionTime.ToUniversalTime();
    }

    private Geometry ReadGeometry(JsonElement root)
    {
        if (_pathReader.TryRead(root, _paths.GeometryWktPath, out var wktElement) &&
            wktElement.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(wktElement.GetString()))
        {
            return _geometryExtractor.ReadWkt(wktElement.GetString()!, "input");
        }

        if (_pathReader.TryRead(root, _paths.GeometryGeoJsonPath, out var geoJsonElement) &&
            geoJsonElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return _geometryExtractor.ReadGeoJson(geoJsonElement, "input");
        }

        throw new NonRetryableGatewayException(
            "Input geometry is required as WKT or GeoJSON.",
            "gateway.missing_geometry");
    }
}

public sealed record ValidatedInputMessage(
    JsonElement OriginalPayload,
    string ImageId,
    string SensorName,
    string? SensorType,
    double Resolution,
    DateTimeOffset? AcquisitionTime,
    Geometry Geometry);
