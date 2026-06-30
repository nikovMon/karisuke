using System.Text.Json;
using ImagingPipeline.ElasticsearchClient;

namespace ImagingPipeline.ElasticsearchClient.Tests;

public sealed class ElasticsearchQueryJsonBuilderTests
{
    [Fact]
    public void BuildSearchBodyUsesMatchAllWhenNoFiltersAreProvided()
    {
        var json = ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            From = 5,
            Size = 25
        });

        using var document = JsonDocument.Parse(json);

        Assert.Equal(5, document.RootElement.GetProperty("from").GetInt32());
        Assert.Equal(25, document.RootElement.GetProperty("size").GetInt32());
        Assert.True(document.RootElement.GetProperty("query").TryGetProperty("match_all", out _));
    }

    [Fact]
    public void BuildSearchBodyCombinesTermTermsSensorAndGeoFilters()
    {
        using var shape = JsonDocument.Parse("""
            {
              "type": "Polygon",
              "coordinates": [[[34.7,32.0],[34.9,32.0],[34.9,32.2],[34.7,32.2],[34.7,32.0]]]
            }
            """);
        var json = ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            TermFilters =
            [
                new ElasticsearchTermFilter { Field = "isActive", Value = true }
            ],
            TermsFilters =
            [
                new ElasticsearchTermsFilter { Field = "algorithmName.keyword", Values = ["FindAir", "Rpn"] }
            ],
            SensorFilters =
            [
                new ElasticsearchSensorFilter
                {
                    SensorName = "camera",
                    Values = ["rgb-main", "rgb-main", "rgb-backup"]
                }
            ],
            GeoShapeFilters =
            [
                new ElasticsearchGeoShapeFilter
                {
                    Field = "locationGeoJson",
                    Shape = shape.RootElement.Clone(),
                    Relation = ElasticsearchGeoShapeRelation.Within
                }
            ]
        });

        using var document = JsonDocument.Parse(json);
        var filters = document.RootElement
            .GetProperty("query")
            .GetProperty("bool")
            .GetProperty("filter")
            .EnumerateArray()
            .ToArray();

        Assert.Equal(4, filters.Length);
        Assert.True(filters[0].GetProperty("term").GetProperty("isActive").GetBoolean());
        Assert.Equal("FindAir", filters[1].GetProperty("terms").GetProperty("algorithmName.keyword")[0].GetString());
        Assert.Equal("sensors.camera.keyword", filters[2].GetProperty("terms").EnumerateObject().Single().Name);
        Assert.Equal(2, filters[2].GetProperty("terms").GetProperty("sensors.camera.keyword").GetArrayLength());
        Assert.Equal("within", filters[3].GetProperty("geo_shape").GetProperty("locationGeoJson").GetProperty("relation").GetString());
        Assert.Equal("Polygon", filters[3].GetProperty("geo_shape").GetProperty("locationGeoJson").GetProperty("shape").GetProperty("type").GetString());
    }

    [Fact]
    public void BuildSearchBodySupportsCustomSensorRootAndNonKeywordField()
    {
        var json = ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "events",
            SensorFilters =
            [
                new ElasticsearchSensorFilter
                {
                    SensorRootField = "metadata.sensors",
                    SensorName = "thermal",
                    Values = ["th-1"],
                    KeywordSuffix = string.Empty
                }
            ]
        });

        using var document = JsonDocument.Parse(json);
        var terms = document.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter")[0].GetProperty("terms");

        Assert.Equal("metadata.sensors.thermal", terms.EnumerateObject().Single().Name);
    }

    [Fact]
    public void BuildSearchBodyRejectsInvalidPagingAndEmptyIndex()
    {
        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = " "
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            From = -1
        }));
        Assert.Throws<ArgumentOutOfRangeException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            Size = 0
        }));
    }

    [Fact]
    public void BuildSearchBodyRejectsInvalidSensorFilters()
    {
        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            SensorFilters =
            [
                new ElasticsearchSensorFilter { SensorName = "", Values = ["cam-1"] }
            ]
        }));
        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            SensorFilters =
            [
                new ElasticsearchSensorFilter { SensorName = "camera", Values = [] }
            ]
        }));
        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            SensorFilters =
            [
                new ElasticsearchSensorFilter { SensorName = "camera", Values = ["cam-1", " "] }
            ]
        }));
    }

    [Fact]
    public void BuildSearchBodyRejectsInvalidGeoFilters()
    {
        using var nullShape = JsonDocument.Parse("null");

        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            GeoShapeFilters =
            [
                new ElasticsearchGeoShapeFilter { Field = "", Shape = nullShape.RootElement.Clone() }
            ]
        }));
        Assert.Throws<ArgumentException>(() => ElasticsearchQueryJsonBuilder.BuildSearchBody(new ElasticsearchSearchRequest
        {
            IndexName = "rules",
            GeoShapeFilters =
            [
                new ElasticsearchGeoShapeFilter { Field = "locationGeoJson", Shape = nullShape.RootElement.Clone() }
            ]
        }));
    }
}
