using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elasticsearch.Net;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Observability;
using Nest;

namespace ImagingPipeline.ElasticsearchClient.Tests;

public sealed class ElasticsearchDocumentClientTests
{
    [Fact]
    public async Task SearchBySensorAsyncReturnsSourcesFromElasticsearchHits()
    {
        var client = CreateDocumentClient("""
            {
              "hits": {
                "hits": [
                  {
                    "_source": {
                      "id": "rule-1",
                      "ruleName": "one",
                      "sensors": {
                        "camera": ["cam-1"]
                      }
                    }
                  },
                  {
                    "_source": {
                      "id": "rule-2",
                      "ruleName": "two",
                      "sensors": {
                        "camera": ["cam-2"]
                      }
                    }
                  }
                ]
              }
            }
            """);

        var results = await client.SearchBySensorAsync<TestRuleDocument>(new ElasticsearchSensorSearchRequest
        {
            IndexName = "rules",
            SensorName = "camera",
            Values = ["cam-1"]
        });

        Assert.Equal(["rule-1", "rule-2"], results.Select(rule => rule.Id));
        Assert.Equal("one", results[0].RuleName);
        Assert.Equal(["cam-1"], results[0].Sensors["camera"]);
    }

    [Fact]
    public async Task SearchAsyncHydratesIdFromHitMetadataWhenSourceIdIsMissing()
    {
        var client = CreateDocumentClient("""
            {
              "hits": {
                "hits": [
                  {
                    "_id": "rule-from-hit",
                    "_source": {
                      "ruleName": "one"
                    }
                  }
                ]
              }
            }
            """);

        var result = Assert.Single(await client.SearchAsync<TestRuleDocument>(new ElasticsearchSearchRequest
        {
            IndexName = "rules"
        }));

        Assert.Equal("rule-from-hit", result.Id);
    }

    [Fact]
    public async Task SearchByGeoShapeAsyncReturnsEmptyArrayWhenThereAreNoHits()
    {
        using var shape = JsonDocument.Parse("{\"type\":\"Point\",\"coordinates\":[1,1]}");
        var client = CreateDocumentClient("""
            {
              "hits": {
                "hits": []
              }
            }
            """);

        var results = await client.SearchByGeoShapeAsync<TestRuleDocument>(new ElasticsearchGeoShapeSearchRequest
        {
            IndexName = "rules",
            Field = "locationGeoJson",
            Shape = shape.RootElement.Clone()
        });

        Assert.Empty(results);
    }

    [Fact]
    public async Task SearchAsyncSkipsHitsWithoutSource()
    {
        var client = CreateDocumentClient("""
            {
              "hits": {
                "hits": [
                  { "_source": null },
                  { "_source": { "id": "rule-1", "ruleName": "one" } }
                ]
              }
            }
            """);

        var results = await client.SearchAsync<TestRuleDocument>(new ElasticsearchSearchRequest
        {
            IndexName = "rules"
        });

        var result = Assert.Single(results);
        Assert.Equal("rule-1", result.Id);
    }

    [Fact]
    public async Task SearchAsyncThrowsClientExceptionWhenElasticsearchReturnsFailure()
    {
        Activity? stoppedActivity = null;
        var metricErrorTypes = new List<string?>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Elasticsearch,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener
        {
            InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == TelemetrySourceNames.Dependencies &&
                    instrument.Name == TelemetryMetricNames.DependencyOperations)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            var tagArray = tags.ToArray();
            if (tagArray.Any(tag =>
                    tag.Key == TelemetryAttributeNames.DependencyOperation &&
                    Equals(tag.Value, "search")))
            {
                metricErrorTypes.Add(tagArray
                    .FirstOrDefault(tag => tag.Key == "error.type")
                    .Value
                    ?.ToString());
            }
        });
        meterListener.Start();

        var client = CreateDocumentClient("""
            {
              "error": {
                "reason": "index missing"
              }
            }
            """, statusCode: 404);

        var exception = await Assert.ThrowsAsync<ElasticsearchClientException>(() =>
            client.SearchAsync<TestRuleDocument>(new ElasticsearchSearchRequest { IndexName = "rules" }));

        Assert.Contains("search index 'rules'", exception.Message, StringComparison.Ordinal);
        Assert.NotNull(stoppedActivity);
        Assert.Equal("search rules", stoppedActivity.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, stoppedActivity.Status);
        Assert.Equal("404", stoppedActivity.GetTagItem("db.response.status_code"));
        Assert.Equal("404", stoppedActivity.GetTagItem("error.type"));
        Assert.Contains("404", metricErrorTypes);
    }

    [Fact]
    public async Task SearchFailureDoesNotCopyLargeResponseBodyOrDebugInformationIntoException()
    {
        var largeReason = "bounded-server-reason-" +
            new string('x', 2_000) +
            "-reason-tail-must-not-be-copied";
        var responseBody = JsonSerializer.Serialize(new
        {
            error = new { reason = largeReason },
            payload = "full-body-secret-marker"
        });
        var client = CreateDocumentClient(responseBody, statusCode: 500);

        var exception = await Assert.ThrowsAsync<ElasticsearchClientException>(() =>
            client.SearchAsync<TestRuleDocument>(new ElasticsearchSearchRequest { IndexName = "rules" }));

        Assert.Contains("bounded-server-reason", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("reason-tail-must-not-be-copied", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("full-body-secret-marker", exception.Message, StringComparison.Ordinal);
        Assert.InRange(exception.Message.Length, 1, 640);
    }

    [Fact]
    public async Task GetAsyncReturnsNullWhenDocumentIsMissing()
    {
        var client = CreateDocumentClient("""
            {
              "found": false
            }
            """, statusCode: 404);

        var result = await client.GetAsync<TestRuleDocument>("rules", "missing");

        Assert.Null(result);
    }

    [Fact]
    public async Task GetDocumentAsyncReturnsElasticsearchIdWithSource()
    {
        var client = CreateDocumentClient("""
            {
              "_id": "elastic-id",
              "found": true,
              "_source": {
                "ruleName": "one"
              }
            }
            """);

        var result = await client.GetDocumentAsync<TestRuleDocument>("rules", "elastic-id");

        Assert.NotNull(result);
        Assert.Equal("elastic-id", result.Id);
        Assert.Equal("one", result.Source.RuleName);
    }

    [Fact]
    public async Task GetDocumentEmitsDependencySpanWithoutUsingDocumentIdAsMetricTag()
    {
        Activity? stoppedActivity = null;
        var metricTagKeys = new List<string>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Elasticsearch,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(activityListener);

        using var meterListener = new MeterListener();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == TelemetrySourceNames.Dependencies)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
        {
            foreach (var tag in tags)
            {
                metricTagKeys.Add(tag.Key);
            }
        });
        meterListener.Start();

        var client = CreateDocumentClient("""
            {
              "_id": "span-only-id",
              "found": true,
              "_source": {
                "ruleName": "one"
              }
            }
            """);

        await client.GetDocumentAsync<TestRuleDocument>("rules", "span-only-id");

        Assert.NotNull(stoppedActivity);
        Assert.Equal("get rules", stoppedActivity.DisplayName);
        Assert.Equal(ActivityKind.Client, stoppedActivity.Kind);
        Assert.Equal("elasticsearch", stoppedActivity.GetTagItem("db.system.name"));
        Assert.Equal("get", stoppedActivity.GetTagItem("db.operation.name"));
        Assert.Equal("rules", stoppedActivity.GetTagItem("db.collection.name"));
        Assert.Equal("GET", stoppedActivity.GetTagItem("http.request.method"));
        Assert.Equal("200", stoppedActivity.GetTagItem("db.response.status_code"));
        Assert.Equal("localhost", stoppedActivity.GetTagItem("server.address"));
        Assert.Equal(9200, stoppedActivity.GetTagItem("server.port"));
        Assert.Equal("span-only-id", stoppedActivity.GetTagItem("elasticsearch.document.id"));
        Assert.Equal(1L, stoppedActivity.GetTagItem("elasticsearch.response.document.count"));
        Assert.Null(stoppedActivity.GetTagItem("db.system"));
        Assert.Null(stoppedActivity.GetTagItem("db.namespace"));
        Assert.Null(stoppedActivity.GetTagItem("db.query.text"));
        Assert.Null(stoppedActivity.GetTagItem("db.statement"));
        var url = Assert.IsType<string>(stoppedActivity.GetTagItem("url.full"));
        var parsedUrl = new Uri(url);
        Assert.Equal("/rules/_doc/span-only-id", parsedUrl.AbsolutePath);
        Assert.Empty(parsedUrl.Query);
        Assert.Empty(parsedUrl.UserInfo);
        Assert.DoesNotContain("elasticsearch.document.id", metricTagKeys);
        Assert.DoesNotContain(TelemetryAttributeNames.PipelineRuleId, metricTagKeys);
    }

    [Fact]
    public async Task IndexSpanScrubsUrlCredentialsAndQueryString()
    {
        Activity? stoppedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Elasticsearch,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(activityListener);
        var client = CreateDocumentClient(
            """
            {
              "_id": "rule-1",
              "result": "created"
            }
            """,
            statusCode: 201,
            nodeUri: new Uri("http://elastic:super-secret@localhost:9200"));

        await client.IndexAsync(
            "rules",
            "rule-1",
            new TestRuleDocument { RuleName = "private-rule-name" },
            waitForRefresh: true);

        Assert.NotNull(stoppedActivity);
        Assert.Equal("index rules", stoppedActivity.DisplayName);
        Assert.Equal("PUT", stoppedActivity.GetTagItem("http.request.method"));
        Assert.Equal("201", stoppedActivity.GetTagItem("db.response.status_code"));
        var url = Assert.IsType<string>(stoppedActivity.GetTagItem("url.full"));
        Assert.DoesNotContain("elastic", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret", url, StringComparison.Ordinal);
        Assert.Empty(new Uri(url).Query);
        Assert.Null(stoppedActivity.GetTagItem("db.query.text"));
    }

    [Fact]
    public async Task SearchDocumentsAsyncReturnsElasticsearchIdsWithSources()
    {
        Activity? stoppedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Elasticsearch,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(activityListener);
        var client = CreateDocumentClient("""
            {
              "hits": {
                "hits": [
                  {
                    "_id": "elastic-id",
                    "_source": {
                      "ruleName": "one"
                    }
                  }
                ]
              }
            }
            """);

        var results = await client.SearchDocumentsAsync<TestRuleDocument>(
            descriptor => descriptor.Index("rules").Size(1).Query(query => query.MatchAll()));

        var result = Assert.Single(results);
        Assert.Equal("elastic-id", result.Id);
        Assert.Equal("one", result.Source.RuleName);
        Assert.NotNull(stoppedActivity);
        Assert.Equal("search rules", stoppedActivity.DisplayName);
        Assert.Equal("rules", stoppedActivity.GetTagItem("db.collection.name"));
        Assert.Equal("POST", stoppedActivity.GetTagItem("http.request.method"));
        Assert.Equal("200", stoppedActivity.GetTagItem("db.response.status_code"));
    }

    [Fact]
    public async Task GetAsyncThrowsClientExceptionWhenElasticsearchFails()
    {
        var client = CreateDocumentClient("""
            {
              "error": {
                "reason": "Elasticsearch unavailable"
              }
            }
            """, statusCode: 503);

        var exception = await Assert.ThrowsAsync<ElasticsearchClientException>(() =>
            client.GetAsync<TestRuleDocument>("rules", "rule-1"));

        Assert.Contains("get document 'rule-1'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAsyncReturnsFalseWhenDocumentIsMissing()
    {
        Activity? stoppedActivity = null;
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetrySourceNames.Elasticsearch,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => stoppedActivity = activity
        };
        ActivitySource.AddActivityListener(activityListener);
        var client = CreateDocumentClient("""
            {
              "result": "not_found"
            }
            """, statusCode: 404);

        var result = await client.DeleteAsync<TestRuleDocument>("rules", "missing");

        Assert.False(result);
        Assert.NotNull(stoppedActivity);
        Assert.Equal("delete rules", stoppedActivity.DisplayName);
        Assert.Equal("DELETE", stoppedActivity.GetTagItem("http.request.method"));
        Assert.Equal("404", stoppedActivity.GetTagItem("db.response.status_code"));
        Assert.Equal("404", stoppedActivity.GetTagItem("error.type"));
        Assert.Equal(ActivityStatusCode.Error, stoppedActivity.Status);
    }

    [Fact]
    public async Task IndexAsyncThrowsForNullDocument()
    {
        var client = CreateDocumentClient("""
            {
              "result": "created"
            }
            """);

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            client.IndexAsync<TestRuleDocument>("rules", "rule-1", null!));
    }

    [Fact]
    public async Task IndexAsyncCanReturnGeneratedElasticsearchId()
    {
        var client = CreateDocumentClient("""
            {
              "_id": "generated-id",
              "result": "created"
            }
            """, statusCode: 201);

        var id = await client.IndexAsync(
            "rules",
            id: null,
            new TestRuleDocument { RuleName = "one" },
            allowGeneratedId: true);

        Assert.Equal("generated-id", id);
    }

    [Fact]
    public async Task GetIndexAndDeleteRejectEmptyIndexOrId()
    {
        var client = CreateDocumentClient("""
            {
              "found": false
            }
            """);

        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync<TestRuleDocument>(" ", "rule-1"));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync<TestRuleDocument>("rules", ""));
        await Assert.ThrowsAsync<ArgumentException>(() => client.IndexAsync("rules", " ", new TestRuleDocument()));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteAsync<TestRuleDocument>("", "rule-1"));
    }

    private static ElasticsearchDocumentClient CreateDocumentClient(
        string responseJson,
        int statusCode = 200,
        Uri? nodeUri = null)
    {
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        var pool = new SingleNodeConnectionPool(nodeUri ?? new Uri("http://localhost:9200"));
        var connection = new InMemoryConnection(bytes, statusCode);
        var settings = new ConnectionSettings(pool, connection)
            .DefaultIndex("rules")
            .DisableDirectStreaming();
        var elasticClient = new ElasticClient(settings);
        return new ElasticsearchDocumentClient(elasticClient);
    }

    private sealed class TestRuleDocument
    {
        [JsonPropertyName("id")]
        public string Id { get; set; } = string.Empty;

        [JsonPropertyName("ruleName")]
        public string RuleName { get; set; } = string.Empty;

        [JsonPropertyName("sensors")]
        public Dictionary<string, List<string>> Sensors { get; set; } = new(StringComparer.Ordinal);
    }
}
