using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.Rules.Api.Services;

internal static class RuleGeometry
{
    public static JsonElement ToGeoJson(Geometry geometry)
    {
        using var document = JsonDocument.Parse(new GeoJsonWriter().Write(geometry));
        return document.RootElement.Clone();
    }
}
