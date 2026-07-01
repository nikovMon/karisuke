using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.GeometryUtils;
using NetTopologySuite.Geometries;

namespace ImagingPipeline.Gateway.Processing.Messages;

public sealed class GatewayGeometryConverter
{
    public Geometry ReadWkt(string wkt, string label)
    {
        try
        {
            return GeometryUtilities.ReadWkt(wkt);
        }
        catch (Exception ex) when (ex is not GatewayValidationException)
        {
            throw new GatewayValidationException($"{label} WKT geometry is invalid: {ex.Message}", "gateway.invalid_geometry");
        }
    }

    public Geometry ReadGeoJson(JsonElement geoJson, string label)
    {
        try
        {
            return GeometryUtilities.ReadGeoJson(geoJson);
        }
        catch (Exception ex) when (ex is not GatewayValidationException)
        {
            throw new GatewayValidationException($"{label} GeoJSON geometry is invalid: {ex.Message}", "gateway.invalid_geometry");
        }
    }

    public Geometry ReadRuleGeometry(RuleConfigDto rule)
    {
        if (!string.IsNullOrWhiteSpace(rule.LocationWkt))
        {
            return ReadWkt(rule.LocationWkt, $"rule '{RuleLabel(rule)}'");
        }

        if (rule.LocationGeoJson.HasValue &&
            rule.LocationGeoJson.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
        {
            return ReadGeoJson(rule.LocationGeoJson.Value, $"rule '{RuleLabel(rule)}'");
        }

        throw new GatewayValidationException(
            $"rule '{RuleLabel(rule)}' does not contain geometry",
            "gateway.rule_missing_geometry");
    }

    public string WriteWkt(Geometry geometry) => GeometryUtilities.WriteWkt(geometry);

    public JsonElement WriteGeoJson(Geometry geometry) => GeometryUtilities.WriteGeoJson(geometry);

    private static string RuleLabel(RuleConfigDto rule) =>
        string.IsNullOrWhiteSpace(rule.Id) ? rule.RuleName : rule.Id;
}
