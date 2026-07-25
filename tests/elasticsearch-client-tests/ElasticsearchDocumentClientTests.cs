using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Elasticsearch.Net;
using ImagingPipeline.ElasticsearchClient;
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
    }

    [Fact]
    public async Task OpenPointInTimeAsyncReturnsIdAndUsesIndexPitEndpoint()
    {
        IApiCallDetails? call = null;
        var client = CreateDocumentClient(
            """{"id":"pit-1"}""",
            onRequestCompleted: details => call = details);

        var result = await client.OpenPointInTimeAsync("rules", "1m");

        Assert.Equal("pit-1", result);
        Assert.NotNull(call);
        Assert.Equal("/rules/_pit", call.Uri.AbsolutePath);
        Assert.Contains("keep_alive=1m", call.Uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("allow_partial_search_results", call.Uri.Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchPointInTimeAsyncReturnsStrictPageMetadataAndStableCursor()
    {
        IApiCallDetails? call = null;
        var client = CreateDocumentClient(
            """
            {
              "pit_id": "pit-2",
              "timed_out": false,
              "_shards": {
                "total": 1,
                "successful": 1,
                "failed": 0
              },
              "hits": {
                "total": {
                  "value": 1,
                  "relation": "eq"
                },
                "hits": [
                  {
                    "_id": "rule-from-hit",
                    "_source": {
                      "ruleName": "one"
                    },
                    "sort": [42]
                  }
                ]
              }
            }
            """,
            onRequestCompleted: details => call = details);

        var page = await client.SearchPointInTimeAsync<TestRuleDocument>(
            new ElasticsearchPointInTimeSearchRequest
            {
                Search = new ElasticsearchSearchRequest
                {
                    IndexName = "rules",
                    Size = 500
                },
                PointInTimeId = "pit-1",
                SearchAfter = [JsonSerializer.SerializeToElement(41L)]
            });

        var hit = Assert.Single(page.Hits);
        Assert.Equal("pit-2", page.PointInTimeId);
        Assert.Equal(1, page.Total);
        Assert.Equal("rule-from-hit", hit.Id);
        Assert.Equal("rule-from-hit", hit.Source.Id);
        Assert.Equal(42, hit.SortValues[0].GetInt64());

        Assert.NotNull(call);
        Assert.Equal("/_search", call.Uri.AbsolutePath);
        Assert.Contains("allow_partial_search_results=false", call.Uri.Query, StringComparison.Ordinal);
        using var request = JsonDocument.Parse(call.RequestBodyInBytes);
        Assert.Equal("pit-1", request.RootElement.GetProperty("pit").GetProperty("id").GetString());
        Assert.Equal("_shard_doc", request.RootElement.GetProperty("sort")[0].GetString());
        Assert.Equal(41, request.RootElement.GetProperty("search_after")[0].GetInt64());
    }

    [Fact]
    public async Task SearchPointInTimeAsyncAllowsOmittedTotalWhenExactTrackingIsDisabled()
    {
        var client = CreateDocumentClient(
            """
            {
              "pit_id": "pit-2",
              "timed_out": false,
              "_shards": {
                "total": 1,
                "successful": 1,
                "failed": 0
              },
              "hits": {
                "hits": [
                  {
                    "_id": "rule-1",
                    "_source": {
                      "ruleName": "one"
                    },
                    "sort": [42]
                  }
                ]
              }
            }
            """);

        var page = await client.SearchPointInTimeAsync<TestRuleDocument>(
            new ElasticsearchPointInTimeSearchRequest
            {
                Search = new ElasticsearchSearchRequest
                {
                    IndexName = "rules",
                    Size = 500
                },
                PointInTimeId = "pit-1",
                TrackTotalHits = false
            });

        Assert.Null(page.Total);
        Assert.Single(page.Hits);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": { "total": 1, "successful": 1, "failed": 0 },
          "hits": {
            "total": { "value": 1, "relation": "eq" },
            "hits": [{ "_id": "rule-1", "_source": null, "sort": [1] }]
          }
        }
        """)]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": { "total": 1, "successful": 1, "failed": 0 },
          "hits": {
            "total": { "value": 1, "relation": "gte" },
            "hits": []
          }
        }
        """)]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": { "total": 1, "successful": 0, "failed": 1 },
          "hits": {
            "total": { "value": 0, "relation": "eq" },
            "hits": []
          }
        }
        """)]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": {},
          "hits": {
            "total": { "value": 0, "relation": "eq" },
            "hits": []
          }
        }
        """)]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": { "total": 1, "successful": 1, "failed": 0 },
          "hits": {
            "total": { "relation": "eq" },
            "hits": []
          }
        }
        """)]
    [InlineData("""
        {
          "pit_id": "pit-2",
          "timed_out": false,
          "_shards": { "total": 0, "successful": 0, "failed": 0 },
          "hits": {
            "total": { "value": 0, "relation": "eq" },
            "hits": []
          }
        }
        """)]
    public async Task SearchPointInTimeAsyncRejectsBlankOrIncompleteResponses(string response)
    {
        var client = CreateDocumentClient(response);

        await Assert.ThrowsAsync<ElasticsearchClientException>(() =>
            client.SearchPointInTimeAsync<TestRuleDocument>(
                new ElasticsearchPointInTimeSearchRequest
                {
                    Search = new ElasticsearchSearchRequest
                    {
                        IndexName = "rules"
                    },
                    PointInTimeId = "pit-1"
                }));
    }

    [Fact]
    public async Task ClosePointInTimeAsyncRequiresSuccessfulResponseAndSendsId()
    {
        IApiCallDetails? call = null;
        var client = CreateDocumentClient(
            """{"succeeded":true,"num_freed":1}""",
            onRequestCompleted: details => call = details);

        await client.ClosePointInTimeAsync("pit-2");

        Assert.NotNull(call);
        Assert.Equal("/_pit", call.Uri.AbsolutePath);
        using var request = JsonDocument.Parse(call.RequestBodyInBytes);
        Assert.Equal("pit-2", request.RootElement.GetProperty("id").GetString());

        var unsuccessful = CreateDocumentClient("""{"succeeded":false,"num_freed":0}""");
        await Assert.ThrowsAsync<ElasticsearchClientException>(() =>
            unsuccessful.ClosePointInTimeAsync("pit-2"));
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
    public async Task SearchDocumentsAsyncReturnsElasticsearchIdsWithSources()
    {
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
        var client = CreateDocumentClient("""
            {
              "result": "not_found"
            }
            """, statusCode: 404);

        var result = await client.DeleteAsync<TestRuleDocument>("rules", "missing");

        Assert.False(result);
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
        Action<IApiCallDetails>? onRequestCompleted = null)
    {
        var bytes = Encoding.UTF8.GetBytes(responseJson);
        var pool = new SingleNodeConnectionPool(new Uri("http://localhost:9200"));
        var connection = new InMemoryConnection(bytes, statusCode);
        var settings = new ConnectionSettings(pool, connection)
            .DefaultIndex("rules")
            .DisableDirectStreaming();
        if (onRequestCompleted is not null)
        {
            settings = settings.OnRequestCompleted(onRequestCompleted);
        }

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
