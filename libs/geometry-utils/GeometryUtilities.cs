using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.GeometryUtils;

public static class GeometryUtilities
{
    public static Geometry ReadWkt(string wkt)
    {
        var geometry = new WKTReader().Read(wkt);
        return Validate(Force2D(geometry));
    }

    public static Geometry ReadGeoJson(JsonElement geoJson)
    {
        var geometry = new GeoJsonReader().Read<Geometry>(geoJson.GetRawText());
        return Validate(Force2D(geometry));
    }

    public static string WriteWkt(Geometry geometry) => new WKTWriter(2).Write(geometry);

    public static JsonElement WriteGeoJson(Geometry geometry)
    {
        var writer = new GeoJsonWriter { Dimension = 2 };
        using var document = JsonDocument.Parse(writer.Write(geometry));
        return document.RootElement.Clone();
    }

    public static Geometry Force2D(Geometry geometry)
    {
        var wkt = WriteWkt(geometry);
        return new WKTReader().Read(wkt);
    }

    public static Geometry Validate(Geometry geometry)
    {
        if (geometry.IsEmpty)
        {
            throw new GeometryValidationException("Geometry is empty.");
        }

        if (!geometry.IsValid)
        {
            throw new GeometryValidationException("Geometry is invalid.");
        }

        return geometry;
    }
}
