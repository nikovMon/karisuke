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
    public List<ElasticsearchTermFilter> TermFilters { get; init; } = [];
    public List<ElasticsearchTermsFilter> TermsFilters { get; init; } = [];
    public List<ElasticsearchSensorFilter> SensorFilters { get; init; } = [];
    public List<ElasticsearchGeoShapeFilter> GeoShapeFilters { get; init; } = [];
}

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
    [JsonPropertyName("_source")]
    public TDocument? Source { get; set; }
}
