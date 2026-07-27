using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.ElasticsearchClient;

public enum ElasticsearchGeoShapeRelation
{
    Intersects,
    Disjoint,
    Within,
    Contains
}

public sealed class ElasticsearchTermFilter
{
    public required string Field { get; init; }
    public required object Value { get; init; }
}

public sealed class ElasticsearchTermsFilter
{
    public required string Field { get; init; }
    public required IReadOnlyCollection<object> Values { get; init; }
}

public sealed class ElasticsearchSensorFilter
{
    public string SensorRootField { get; init; } = "sensors";
    public required string SensorName { get; init; }
    public required IReadOnlyCollection<string> Values { get; init; }
    public string KeywordSuffix { get; init; } = ".keyword";
}

public sealed class ElasticsearchGeoShapeFilter
{
    public required string Field { get; init; }
    public required JsonElement Shape { get; init; }
    public ElasticsearchGeoShapeRelation Relation { get; init; } = ElasticsearchGeoShapeRelation.Intersects;
}

public sealed class ElasticsearchSearchRequest
{
    public required string IndexName { get; init; }
    public int From { get; init; }
    public int Size { get; init; } = 100;
    public List<string> SourceIncludes { get; init; } = [];
    public List<string> ExcludedIds { get; init; } = [];
    public List<ElasticsearchTermFilter> TermFilters { get; init; } = [];
    public List<ElasticsearchTermsFilter> TermsFilters { get; init; } = [];
    public List<ElasticsearchSensorFilter> SensorFilters { get; init; } = [];
    public List<ElasticsearchGeoShapeFilter> GeoShapeFilters { get; init; } = [];
}

public sealed class ElasticsearchPointInTimeSearchRequest
{
    public required ElasticsearchSearchRequest Search { get; init; }
    public required string PointInTimeId { get; init; }
    public string KeepAlive { get; init; } = "1m";
    public IReadOnlyList<JsonElement> SearchAfter { get; init; } = [];
    public bool TrackTotalHits { get; init; } = true;
}

public sealed record ElasticsearchSearchHit<TDocument>(
    string Id,
    TDocument Source,
    IReadOnlyList<JsonElement> SortValues)
    where TDocument : class;

public sealed record ElasticsearchSearchPage<TDocument>(
    string PointInTimeId,
    long? Total,
    IReadOnlyList<ElasticsearchSearchHit<TDocument>> Hits)
    where TDocument : class;

public sealed class ElasticsearchSensorSearchRequest
{
    public required string IndexName { get; init; }
    public string SensorRootField { get; init; } = "sensors";
    public required string SensorName { get; init; }
    public required IReadOnlyCollection<string> Values { get; init; }
    public string KeywordSuffix { get; init; } = ".keyword";
    public int From { get; init; }
    public int Size { get; init; } = 100;
}

public sealed class ElasticsearchGeoShapeSearchRequest
{
    public required string IndexName { get; init; }
    public required string Field { get; init; }
    public required JsonElement Shape { get; init; }
    public ElasticsearchGeoShapeRelation Relation { get; init; } = ElasticsearchGeoShapeRelation.Intersects;
    public int From { get; init; }
    public int Size { get; init; } = 100;
}

public static class ElasticsearchQueryJsonBuilder
{
    public static string BuildSearchBody(ElasticsearchSearchRequest request)
    {
        Validate(request);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("from", request.From);
            writer.WriteNumber("size", request.Size);
            WriteSourceIncludes(writer, request);
            writer.WritePropertyName("query");
            WriteQuery(writer, request);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    public static string BuildPointInTimeSearchBody(ElasticsearchPointInTimeSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Search);
        Validate(request.Search);

        if (request.Search.From != 0)
        {
            throw new ArgumentException(
                "Point-in-time searches do not support a non-zero From value.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.PointInTimeId))
        {
            throw new ArgumentException("PointInTimeId must not be empty.", nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.KeepAlive))
        {
            throw new ArgumentException("KeepAlive must not be empty.", nameof(request));
        }

        if (request.SearchAfter.Any(value =>
            value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined))
        {
            throw new ArgumentException(
                "SearchAfter values must not be null or undefined.",
                nameof(request));
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("size", request.Search.Size);
            writer.WriteBoolean("track_total_hits", request.TrackTotalHits);
            WriteSourceIncludes(writer, request.Search);

            writer.WriteStartObject("pit");
            writer.WriteString("id", request.PointInTimeId);
            writer.WriteString("keep_alive", request.KeepAlive);
            writer.WriteEndObject();

            writer.WritePropertyName("sort");
            writer.WriteStartArray();
            writer.WriteStringValue("_shard_doc");
            writer.WriteEndArray();

            if (request.SearchAfter.Count > 0)
            {
                writer.WritePropertyName("search_after");
                writer.WriteStartArray();
                foreach (var value in request.SearchAfter)
                {
                    value.WriteTo(writer);
                }

                writer.WriteEndArray();
            }

            writer.WritePropertyName("query");
            WriteQuery(writer, request.Search);
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Validate(ElasticsearchSearchRequest request)
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

        foreach (var field in request.SourceIncludes)
        {
            RequireField(field, "Source include field must not be empty.", request);
        }

        foreach (var id in request.ExcludedIds)
        {
            RequireField(id, "Excluded id must not be empty.", request);
        }

        foreach (var filter in request.TermFilters)
        {
            RequireField(filter.Field, "Term filter field must not be empty.", request);
        }

        foreach (var filter in request.TermsFilters)
        {
            RequireField(filter.Field, "Terms filter field must not be empty.", request);
            if (filter.Values.Count == 0)
            {
                throw new ArgumentException("Terms filter values must not be empty.", nameof(request));
            }
        }

        foreach (var filter in request.SensorFilters)
        {
            RequireField(filter.SensorRootField, "Sensor root field must not be empty.", request);
            RequireField(filter.SensorName, "Sensor name must not be empty.", request);
            if (filter.Values.Count == 0 || filter.Values.Any(string.IsNullOrWhiteSpace))
            {
                throw new ArgumentException("Sensor values must not be empty.", nameof(request));
            }
        }

        foreach (var filter in request.GeoShapeFilters)
        {
            RequireField(filter.Field, "Geo shape field must not be empty.", request);
            if (filter.Shape.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new ArgumentException("Geo shape must not be null.", nameof(request));
            }
        }
    }

    private static void RequireField(
        string field,
        string error,
        ElasticsearchSearchRequest request)
    {
        if (string.IsNullOrWhiteSpace(field))
        {
            throw new ArgumentException(error, nameof(request));
        }
    }

    private static void WriteSourceIncludes(Utf8JsonWriter writer, ElasticsearchSearchRequest request)
    {
        if (request.SourceIncludes.Count == 0)
        {
            return;
        }

        writer.WritePropertyName("_source");
        writer.WriteStartArray();
        foreach (var field in request.SourceIncludes)
        {
            writer.WriteStringValue(field);
        }

        writer.WriteEndArray();
    }

    private static void WriteQuery(Utf8JsonWriter writer, ElasticsearchSearchRequest request)
    {
        if (!HasFilters(request))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("match_all");
            writer.WriteEndObject();
            writer.WriteEndObject();
            return;
        }

        writer.WriteStartObject();
        writer.WriteStartObject("bool");

        if (HasPositiveFilters(request))
        {
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
                var field = $"{filter.SensorRootField}.{filter.SensorName}{filter.KeywordSuffix}";
                var values = filter.Values.Distinct(StringComparer.Ordinal).Cast<object>().ToArray();
                WriteTermsFilter(writer, field, values);
            }

            foreach (var filter in request.GeoShapeFilters)
            {
                WriteGeoShapeFilter(writer, filter);
            }

            writer.WriteEndArray();
        }

        if (request.ExcludedIds.Count > 0)
        {
            writer.WritePropertyName("must_not");
            writer.WriteStartArray();
            writer.WriteStartObject();
            writer.WritePropertyName("ids");
            writer.WriteStartObject();
            writer.WritePropertyName("values");
            JsonSerializer.Serialize(writer, request.ExcludedIds);
            writer.WriteEndObject();
            writer.WriteEndObject();
            writer.WriteEndArray();
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static bool HasFilters(ElasticsearchSearchRequest request) =>
        HasPositiveFilters(request) ||
        request.ExcludedIds.Count > 0;

    private static bool HasPositiveFilters(ElasticsearchSearchRequest request) =>
        request.TermFilters.Count > 0 ||
        request.TermsFilters.Count > 0 ||
        request.SensorFilters.Count > 0 ||
        request.GeoShapeFilters.Count > 0;

    private static void WriteTermFilter(Utf8JsonWriter writer, string field, object value)
    {
        writer.WriteStartObject();
        writer.WriteStartObject("term");
        writer.WritePropertyName(field);
        JsonSerializer.Serialize(writer, value);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteTermsFilter(
        Utf8JsonWriter writer,
        string field,
        IReadOnlyCollection<object> values)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("terms");
        writer.WriteStartObject();
        writer.WritePropertyName(field);
        JsonSerializer.Serialize(writer, values);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteGeoShapeFilter(
        Utf8JsonWriter writer,
        ElasticsearchGeoShapeFilter filter)
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

internal sealed class ElasticsearchSearchResponse<TDocument>
{
    [JsonPropertyName("hits")]
    public ElasticsearchHits<TDocument>? Hits { get; set; }
}

internal sealed class ElasticsearchHits<TDocument>
{
    [JsonPropertyName("hits")]
    public List<ElasticsearchHit<TDocument>> Items { get; set; } = [];
}

internal sealed class ElasticsearchHit<TDocument>
{
    [JsonPropertyName("_id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("_source")]
    public TDocument? Source { get; set; }
}

internal sealed class ElasticsearchOpenPointInTimeResponse
{
    [JsonPropertyName("id")]
    public string? Id { get; set; }
}

internal sealed class ElasticsearchClosePointInTimeResponse
{
    [JsonPropertyName("succeeded")]
    public bool Succeeded { get; set; }
}

internal sealed class ElasticsearchPointInTimeSearchResponse<TDocument>
    where TDocument : class
{
    [JsonPropertyName("pit_id")]
    public string? PointInTimeId { get; set; }

    [JsonPropertyName("timed_out")]
    public bool? TimedOut { get; set; }

    [JsonPropertyName("_shards")]
    public ElasticsearchShardSummary? Shards { get; set; }

    [JsonPropertyName("hits")]
    public ElasticsearchPointInTimeHits<TDocument>? Hits { get; set; }
}

internal sealed class ElasticsearchShardSummary
{
    [JsonPropertyName("total")]
    public int? Total { get; set; }

    [JsonPropertyName("successful")]
    public int? Successful { get; set; }

    [JsonPropertyName("failed")]
    public int? Failed { get; set; }
}

internal sealed class ElasticsearchPointInTimeHits<TDocument>
    where TDocument : class
{
    [JsonPropertyName("total")]
    public ElasticsearchTotalHits? Total { get; set; }

    [JsonPropertyName("hits")]
    public List<ElasticsearchPointInTimeHit<TDocument>>? Items { get; set; }
}

internal sealed class ElasticsearchTotalHits
{
    [JsonPropertyName("value")]
    public long? Value { get; set; }

    [JsonPropertyName("relation")]
    public string? Relation { get; set; }
}

internal sealed class ElasticsearchPointInTimeHit<TDocument>
    where TDocument : class
{
    [JsonPropertyName("_id")]
    public string? Id { get; set; }

    [JsonPropertyName("_source")]
    public TDocument? Source { get; set; }

    [JsonPropertyName("sort")]
    public List<JsonElement>? SortValues { get; set; }
}
