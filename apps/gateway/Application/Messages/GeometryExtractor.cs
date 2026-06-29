using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Domain;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.Gateway.Application.Messages;

public sealed class GeometryExtractor
{
    public Geometry ReadWkt(string wkt, string label)
    {
        try
        {
            var geometry = new WKTReader().Read(wkt);
            return ValidateGeometry(Force2D(geometry), label);
        }
        catch (Exception ex) when (ex is not NonRetryableGatewayException)
        {
            throw new NonRetryableGatewayException($"{label} WKT geometry is invalid: {ex.Message}", "gateway.invalid_geometry");
        }
    }

    public Geometry ReadGeoJson(JsonElement geoJson, string label)
    {
        try
        {
            var json = geoJson.GetRawText();
            var geometry = new GeoJsonReader().Read<Geometry>(json);
            return ValidateGeometry(Force2D(geometry), label);
        }
        catch (Exception ex) when (ex is not NonRetryableGatewayException)
        {
            throw new NonRetryableGatewayException($"{label} GeoJSON geometry is invalid: {ex.Message}", "gateway.invalid_geometry");
        }
    }

    public Geometry ReadRuleGeometry(RuleConfigDto rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.Wkt))
        {
            return ReadWkt(rule.Wkt, $"rule '{RuleLabel(rule)}'");
        }

        if (rule.GeoJson.HasValue &&
            rule.GeoJson.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return ReadGeoJson(rule.GeoJson.Value, $"rule '{RuleLabel(rule)}'");
        }

        throw new NonRetryableGatewayException(
            $"rule '{RuleLabel(rule)}' does not contain geometry",
            "gateway.rule_missing_geometry");
    }

    public string WriteWkt(Geometry geometry) => new WKTWriter(2).Write(geometry);

    public JsonElement WriteGeoJson(Geometry geometry)
    {
        var writer = new GeoJsonWriter { Dimension = 2 };
        using var document = JsonDocument.Parse(writer.Write(geometry));
        return document.RootElement.Clone();
    }

    private static Geometry ValidateGeometry(Geometry geometry, string label)
    {
        if (geometry.IsEmpty)
        {
            throw new NonRetryableGatewayException($"{label} geometry is empty", "gateway.invalid_geometry");
        }

        if (!geometry.IsValid)
        {
            throw new NonRetryableGatewayException($"{label} geometry is invalid", "gateway.invalid_geometry");
        }

        return geometry;
    }

    private static Geometry Force2D(Geometry geometry)
    {
        var wkt = new WKTWriter(2).Write(geometry);
        return new WKTReader().Read(wkt);
    }

    private static string RuleLabel(RuleConfigDto rule) =>
        string.IsNullOrWhiteSpace(rule.Id) ? rule.RuleName : rule.Id;
}
