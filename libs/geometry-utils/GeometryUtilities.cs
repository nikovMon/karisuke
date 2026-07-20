using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.GeometryUtils;

public static class GeometryUtilities
{
    public static Geometry ReadWkt(string wkt)
    {
        return Validate(ReadTrustedWkt(wkt));
    }

    public static Geometry ReadGeoJson(JsonElement geoJson)
    {
        return Validate(ReadTrustedGeoJson(geoJson));
    }

    public static Geometry ReadTrustedWkt(string wkt) =>
        Force2D(new WKTReader().Read(wkt));

    public static Geometry ReadTrustedGeoJson(JsonElement geoJson) =>
        Force2D(new GeoJsonReader().Read<Geometry>(geoJson.GetRawText()));

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
