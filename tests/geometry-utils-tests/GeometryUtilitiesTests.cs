using System.Buffers;
using System.Text.Json;
using NetTopologySuite;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.GeometryUtils.Tests;

public sealed class GeometryUtilitiesTests
{
    [Fact]
    public void Force2D_RemovesZWithoutChangingTheSourceOrGeometryStructure()
    {
        var geometryServices = new NtsGeometryServices(new PrecisionModel(1_000), 3857);
        var source = new WKTReader(geometryServices).Read(
            """
            GEOMETRYCOLLECTION Z (
              POINT Z (1 2 3),
              LINESTRING Z (0 0 4, 1 1 5),
              POLYGON Z (
                (0 0 6, 4 0 7, 4 4 8, 0 4 9, 0 0 6),
                (1 1 10, 1 2 11, 2 2 12, 2 1 13, 1 1 10)
              )
            )
            """);

        var result = GeometryUtilities.Force2D(source);

        Assert.NotSame(source, result);
        Assert.Same(source.Factory, result.Factory);
        Assert.Equal(3857, result.SRID);
        Assert.Equal(source.GeometryType, result.GeometryType);
        Assert.Equal(source.NumGeometries, result.NumGeometries);
        Assert.All(source.Coordinates, coordinate => Assert.False(double.IsNaN(coordinate.Z)));
        Assert.All(result.Coordinates, coordinate => Assert.True(double.IsNaN(coordinate.Z)));
        Assert.Equal(Ordinates.XY, GetCoordinateOrdinates(result));
        Assert.Equal(
            "GEOMETRYCOLLECTION (POINT (1 2), LINESTRING (0 0, 1 1), "
            + "POLYGON ((0 0, 4 0, 4 4, 0 4, 0 0), (1 1, 1 2, 2 2, 2 1, 1 1)))",
            GeometryUtilities.WriteWkt(result));
    }

    [Fact]
    public void Force2D_DoesNotChangeValidationSemantics()
    {
        var reader = new WKTReader();
        var invalid = reader.Read("POLYGON Z ((0 0 1, 2 2 2, 0 2 3, 2 0 4, 0 0 1))");
        var empty = reader.Read("POLYGON Z EMPTY");

        var invalidResult = GeometryUtilities.Force2D(invalid);
        var emptyResult = GeometryUtilities.Force2D(empty);

        Assert.False(invalidResult.IsValid);
        Assert.True(emptyResult.IsEmpty);
        Assert.IsType<Polygon>(emptyResult);
        Assert.Equal(invalid.SRID, invalidResult.SRID);
        Assert.Equal(empty.SRID, emptyResult.SRID);
    }

    [Fact]
    public void WriteGeoJson_WritesEverySupportedGeometryTypeDirectlyAsTwoDimensionalJson()
    {
        var reader = new WKTReader();
        Geometry[] geometries =
        [
            reader.Read("POINT Z (1 2 3)"),
            reader.Read("LINESTRING Z (0 0 1, 1 1 2)"),
            reader.Read(
                "POLYGON Z ((0 0 1, 4 0 2, 4 4 3, 0 4 4, 0 0 1), "
                + "(1 1 5, 1 2 6, 2 2 7, 2 1 8, 1 1 5))"),
            reader.Read(
                "POLYGON Z ((0 0 1, 0 4 2, 4 4 3, 4 0 4, 0 0 1), "
                + "(1 1 5, 2 1 6, 2 2 7, 1 2 8, 1 1 5))"),
            reader.Read("MULTIPOINT Z ((1 2 3), (4 5 6))"),
            reader.Read("MULTILINESTRING Z ((0 0 1, 1 1 2), (2 2 3, 3 3 4))"),
            reader.Read(
                "MULTIPOLYGON Z (((0 0 1, 2 0 2, 2 2 3, 0 2 4, 0 0 1)), "
                + "((3 3 5, 5 3 6, 5 5 7, 3 5 8, 3 3 5)))"),
            reader.Read(
                "GEOMETRYCOLLECTION Z (POINT Z (1 2 3), "
                + "LINESTRING Z (0 0 4, 1 1 5))"),
            reader.Read("POINT EMPTY"),
            reader.Read("LINESTRING EMPTY"),
            reader.Read("POLYGON EMPTY"),
            reader.Read("MULTIPOINT EMPTY"),
            reader.Read("MULTILINESTRING EMPTY"),
            reader.Read("MULTIPOLYGON EMPTY"),
            reader.Read("GEOMETRYCOLLECTION EMPTY")
        ];

        foreach (var geometry in geometries)
        {
            using var expected = WriteWithNts(geometry);
            using var actual = WriteDirectly(geometry);

            Assert.True(
                JsonElement.DeepEquals(expected.RootElement, actual.RootElement),
                $"Direct GeoJSON differed for {geometry.GeometryType}.{Environment.NewLine}"
                + $"Expected: {expected.RootElement.GetRawText()}{Environment.NewLine}"
                + $"Actual: {actual.RootElement.GetRawText()}");
        }
    }

    [Fact]
    public void WriteGeoJson_CanWriteInsideAnExistingJsonObject()
    {
        var geometry = new WKTReader().Read("POINT Z (12.5 34.5 99)");
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", "image-1");
            writer.WritePropertyName("roiFootprint");
            GeometryUtilities.WriteGeoJson(writer, geometry);
            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        Assert.Equal("image-1", document.RootElement.GetProperty("id").GetString());
        Assert.Equal(
            """{"type":"Point","coordinates":[12.5,34.5]}""",
            document.RootElement.GetProperty("roiFootprint").GetRawText());
    }

    private static Ordinates GetCoordinateOrdinates(Geometry geometry)
    {
        var filter = new CoordinateOrdinatesFilter();
        geometry.Apply(filter);
        return filter.Ordinates;
    }

    private static JsonDocument WriteDirectly(Geometry geometry)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            GeometryUtilities.WriteGeoJson(writer, geometry);
        }

        return JsonDocument.Parse(buffer.WrittenMemory);
    }

    private static JsonDocument WriteWithNts(Geometry geometry)
    {
        var writer = new GeoJsonWriter { Dimension = 2 };
        return JsonDocument.Parse(writer.Write(geometry));
    }

    private sealed class CoordinateOrdinatesFilter : ICoordinateSequenceFilter
    {
        public Ordinates Ordinates { get; private set; } = Ordinates.XY;

        public bool Done => false;

        public bool GeometryChanged => false;

        public void Filter(CoordinateSequence sequence, int index)
        {
            Ordinates |= sequence.Ordinates;
        }
    }
}
