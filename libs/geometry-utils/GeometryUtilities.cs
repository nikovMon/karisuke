using System.Buffers;
using System.Text.Json;
using NetTopologySuite.Algorithm;
using NetTopologySuite.Geometries;
using NetTopologySuite.Geometries.Utilities;
using NetTopologySuite.IO;

namespace ImagingPipeline.GeometryUtils;

public static class GeometryUtilities
{
    private static readonly GeometryEditor.CoordinateSequenceOperation Force2DOperation = new(
        static (source, geometry) =>
        {
            var result = geometry.Factory.CoordinateSequenceFactory.Create(source.Count, Ordinates.XY);
            for (var index = 0; index < source.Count; index++)
            {
                result.SetX(index, source.GetX(index));
                result.SetY(index, source.GetY(index));
            }

            return result;
        });

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

    public static Geometry CreatePolygonFromCoordinates(IReadOnlyList<IReadOnlyList<double>> points)
    {
        if (points.Count < 3)
        {
            throw new GeometryValidationException("At least 3 coordinates are required to build a polygon.");
        }

        var coordinates = points.Select(point => new Coordinate(point[0], point[1])).ToList();
        if (!coordinates[0].Equals2D(coordinates[^1]))
        {
            coordinates.Add(coordinates[0]);
        }

        var geometry = new GeometryFactory().CreatePolygon([.. coordinates]);
        return Validate(Force2D(geometry));
    }

    public static string WriteWkt(Geometry geometry) => new WKTWriter(2).Write(geometry);

    public static JsonElement WriteGeoJson(Geometry geometry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteGeoJson(writer, geometry);
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    public static void WriteGeoJson(Utf8JsonWriter writer, Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(geometry);

        writer.WriteStartObject();

        switch (geometry)
        {
            case Point point:
                writer.WriteString("type", "Point");
                writer.WritePropertyName("coordinates");
                WritePointCoordinates(writer, point);
                break;
            case LinearRing ring:
                writer.WriteString("type", "LineString");
                writer.WritePropertyName("coordinates");
                WriteCoordinateSequence(writer, ring.CoordinateSequence);
                break;
            case LineString lineString:
                writer.WriteString("type", "LineString");
                writer.WritePropertyName("coordinates");
                WriteCoordinateSequence(writer, lineString.CoordinateSequence);
                break;
            case Polygon polygon:
                writer.WriteString("type", "Polygon");
                writer.WritePropertyName("coordinates");
                WritePolygonCoordinates(writer, polygon);
                break;
            case MultiPoint multiPoint:
                writer.WriteString("type", "MultiPoint");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var index = 0; index < multiPoint.NumGeometries; index++)
                {
                    WritePointCoordinates(writer, (Point)multiPoint.GetGeometryN(index));
                }

                writer.WriteEndArray();
                break;
            case MultiLineString multiLineString:
                writer.WriteString("type", "MultiLineString");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var index = 0; index < multiLineString.NumGeometries; index++)
                {
                    WriteCoordinateSequence(
                        writer,
                        ((LineString)multiLineString.GetGeometryN(index)).CoordinateSequence);
                }

                writer.WriteEndArray();
                break;
            case MultiPolygon multiPolygon:
                writer.WriteString("type", "MultiPolygon");
                writer.WritePropertyName("coordinates");
                writer.WriteStartArray();
                for (var index = 0; index < multiPolygon.NumGeometries; index++)
                {
                    WritePolygonCoordinates(writer, (Polygon)multiPolygon.GetGeometryN(index));
                }

                writer.WriteEndArray();
                break;
            case GeometryCollection collection:
                writer.WriteString("type", "GeometryCollection");
                writer.WritePropertyName("geometries");
                writer.WriteStartArray();
                for (var index = 0; index < collection.NumGeometries; index++)
                {
                    WriteGeoJson(writer, collection.GetGeometryN(index));
                }

                writer.WriteEndArray();
                break;
            default:
                throw new NotSupportedException(
                    $"GeoJSON serialization is not supported for geometry type '{geometry.GeometryType}'.");
        }

        writer.WriteEndObject();
    }

    public static Geometry Force2D(Geometry geometry)
    {
        ArgumentNullException.ThrowIfNull(geometry);
        return new GeometryEditor().Edit(geometry, Force2DOperation);
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

    private static void WritePolygonCoordinates(Utf8JsonWriter writer, Polygon polygon)
    {
        writer.WriteStartArray();
        if (polygon.IsEmpty)
        {
            writer.WriteEndArray();
            return;
        }

        WriteRingCoordinates(writer, polygon.ExteriorRing.CoordinateSequence, counterClockwise: true);

        for (var index = 0; index < polygon.NumInteriorRings; index++)
        {
            WriteRingCoordinates(
                writer,
                polygon.GetInteriorRingN(index).CoordinateSequence,
                counterClockwise: false);
        }

        writer.WriteEndArray();
    }

    private static void WritePointCoordinates(Utf8JsonWriter writer, Point point)
    {
        if (point.IsEmpty)
        {
            writer.WriteStartArray();
            writer.WriteEndArray();
            return;
        }

        WriteCoordinate(writer, point.CoordinateSequence, 0);
    }

    private static void WriteCoordinateSequence(Utf8JsonWriter writer, CoordinateSequence coordinates)
    {
        WriteCoordinateSequence(writer, coordinates, reverse: false);
    }

    private static void WriteRingCoordinates(
        Utf8JsonWriter writer,
        CoordinateSequence coordinates,
        bool counterClockwise)
    {
        var reverse = Orientation.IsCCW(coordinates) != counterClockwise;
        WriteCoordinateSequence(writer, coordinates, reverse);
    }

    private static void WriteCoordinateSequence(
        Utf8JsonWriter writer,
        CoordinateSequence coordinates,
        bool reverse)
    {
        writer.WriteStartArray();
        if (reverse)
        {
            for (var index = coordinates.Count - 1; index >= 0; index--)
            {
                WriteCoordinate(writer, coordinates, index);
            }
        }
        else
        {
            for (var index = 0; index < coordinates.Count; index++)
            {
                WriteCoordinate(writer, coordinates, index);
            }
        }

        writer.WriteEndArray();
    }

    private static void WriteCoordinate(
        Utf8JsonWriter writer,
        CoordinateSequence coordinates,
        int index)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(coordinates.GetX(index));
        writer.WriteNumberValue(coordinates.GetY(index));
        writer.WriteEndArray();
    }
}
