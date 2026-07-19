using System.Text.Json;
using NetTopologySuite.IO;

namespace ImagingPipeline.Rules.Api.Services;

internal static class RuleGeometry
{
    public static bool ConvertWktToGeoJson(
        string? wkt,
        out JsonElement geoJson,
        out string? error)
    {
        geoJson = default;

        if (string.IsNullOrWhiteSpace(wkt))
        {
            error = "locationWkt is required.";
            return false;
        }

        try
        {
            var geometry = new WKTReader().Read(wkt);
            if (geometry.IsEmpty || !geometry.IsValid)
            {
                error = "locationWkt must contain a valid, non-empty WKT geometry.";
                return false;
            }

            var json = new GeoJsonWriter().Write(geometry);
            geoJson = JsonDocument.Parse(json).RootElement.Clone();
            error = null;
            return true;
        }
        catch (Exception exception) when (exception is ParseException or ArgumentException)
        {
            error = "locationWkt must contain valid WKT.";
            return false;
        }
    }
}
