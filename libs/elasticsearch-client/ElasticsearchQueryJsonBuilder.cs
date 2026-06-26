using System.Text.Json;

namespace ImagingPipeline.ElasticsearchClient;

public static class ElasticsearchQueryJsonBuilder
{
    public static string BuildSearchBody(ElasticsearchSearchRequest request)
    {
        ValidateSearchRequest(request);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from", request.From);
            writer.WriteNumber("size", request.Size);
            writer.WritePropertyName("query");
            WriteQuery(writer, request);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    internal static void ValidateSearchRequest(ElasticsearchSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.IndexName))
        {
            throw new ArgumentException("IndexName must not be empty.", nameof(request));
        }

        if (request.From < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "From must be greater than or equal to 0.");
        }

        if (request.Size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request), "Size must be greater than 0.");
        }

        foreach (var filter in request.TermFilters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field))
            {
                throw new ArgumentException("Term filter field must not be empty.", nameof(request));
            }
        }

        foreach (var filter in request.TermsFilters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field))
            {
                throw new ArgumentException("Terms filter field must not be empty.", nameof(request));
            }

            if (filter.Values.Count == 0)
            {
                throw new ArgumentException("Terms filter values must not be empty.", nameof(request));
            }
        }

        foreach (var filter in request.SensorFilters)
        {
            if (string.IsNullOrWhiteSpace(filter.SensorRootField))
            {
                throw new ArgumentException("Sensor root field must not be empty.", nameof(request));
            }

            if (string.IsNullOrWhiteSpace(filter.SensorName))
            {
                throw new ArgumentException("Sensor name must not be empty.", nameof(request));
            }

            if (filter.Values.Count == 0 || filter.Values.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Sensor values must not be empty.", nameof(request));
            }
        }

        foreach (var filter in request.GeoShapeFilters)
        {
            if (string.IsNullOrWhiteSpace(filter.Field))
            {
                throw new ArgumentException("Geo shape field must not be empty.", nameof(request));
            }

            if (filter.Shape.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new ArgumentException("Geo shape must not be null.", nameof(request));
            }
        }
    }

    private static void WriteQuery(Utf8JsonWriter writer, ElasticsearchSearchRequest request)
    {
        var hasFilters = request.TermFilters.Count > 0 ||
            request.TermsFilters.Count > 0 ||
            request.SensorFilters.Count > 0 ||
            request.GeoShapeFilters.Count > 0;

        if (!hasFilters)
        {
            writer.WriteStartObject();
            writer.WriteStartObject("match_all");
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartObject("bool");
        writer.WritePropertyName("filter");
        writer.WriteStartArray();

        foreach (var filter in request.TermFilters)
        {
            WriteTermFilter(writer, filter.Field, filter.Value);
        }

        foreach (var filter in request.TermsFilters)
        {
            WriteTermsFilter(writer, filter.Field, filter.Values);
        }

        foreach (var filter in request.SensorFilters)
        {
            var field = BuildSensorField(filter);
            WriteTermsFilter(writer, field, filter.Values.Distinct(StringComparer.Ordinal).Cast<object>().ToArray());
        }

        foreach (var filter in request.GeoShapeFilters)
        {
            WriteGeoShapeFilter(writer, filter);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string BuildSensorField(ElasticsearchSensorFilter filter) =>
        $"{filter.SensorRootField}.{filter.SensorName}{filter.KeywordSuffix}";

    private static void WriteTermFilter(Utf8JsonWriter writer, string field, object value)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("term");
        writer.WritePropertyName(field);
        JsonSerializer.Serialize(writer, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTermsFilter(Utf8JsonWriter writer, string field, IReadOnlyCollection<object> values)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("terms");
        writer.WriteStartObject();
        writer.WritePropertyName(field);
        JsonSerializer.Serialize(writer, values);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteGeoShapeFilter(Utf8JsonWriter writer, ElasticsearchGeoShapeFilter filter)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("geo_shape");
        writer.WriteStartObject();
        writer.WritePropertyName(filter.Field);
        writer.WriteStartObject();
        writer.WritePropertyName("shape");
        filter.Shape.WriteTo(writer);
        writer.WriteString("relation", ToRelationValue(filter.Relation));
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static string ToRelationValue(ElasticsearchGeoShapeRelation relation) =>
        relation switch
        {
            ElasticsearchGeoShapeRelation.Intersects => "intersects",
            ElasticsearchGeoShapeRelation.Disjoint => "disjoint",
            ElasticsearchGeoShapeRelation.Within => "within",
            ElasticsearchGeoShapeRelation.Contains => "contains",
            _ => throw new ArgumentOutOfRangeException(nameof(relation), relation, "Unsupported geo shape relation.")
        };
}
