using System.Text;
using System.Text.Json;
using Elasticsearch.Net;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Repositories;
using Microsoft.Extensions.Options;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class ElasticsearchRuleRepositoryTests
{
    [Fact]
    public async Task SaveLetsElasticsearchGenerateIdAndOmitsMetadataIdFromSource()
    {
        byte[]? requestBody = null;
        Uri? requestUri = null;
        var responseBody = Encoding.UTF8.GetBytes("""
            {
              "_index": "rules",
              "_type": "_doc",
              "_id": "elastic-generated-id",
              "_version": 1,
              "result": "created",
              "_shards": { "total": 1, "successful": 1, "failed": 0 },
              "_seq_no": 0,
              "_primary_term": 1
            }
            """);
        var repository = CreateRepository(
            responseBody,
            201,
            call =>
            {
                requestBody = call.RequestBodyInBytes;
                requestUri = call.Uri;
            });
        var rule = ValidRule(id: string.Empty);

        await repository.SaveAsync(rule);

        Assert.Equal("elastic-generated-id", rule.Id);
        Assert.NotNull(requestUri);
        Assert.Equal("/rules/_doc", requestUri.AbsolutePath);
        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        Assert.False(document.RootElement.TryGetProperty("_id", out _));
        Assert.False(document.RootElement.TryGetProperty("id", out _));
        Assert.Equal("FindAir", document.RootElement.GetProperty("algorithmName").GetString());
        Assert.Equal(1, document.RootElement.GetProperty("maximumResolution").GetDouble());
        Assert.Equal("tenant-1",
            document.RootElement.GetProperty("tenantsInfo")[0].GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task GetByIdHydratesElasticsearchMetadataId()
    {
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "_index": "rules",
                  "_type": "_doc",
                  "_id": "elastic-id",
                  "_version": 1,
                  "_seq_no": 0,
                  "_primary_term": 1,
                  "found": true,
                  "_source": {
                    "ruleName": "one",
                    "algorithmName": "FindAir",
                    "sensors": {},
                    "isActive": true,
                    "tenantsInfo": [],
                    "minimumResolution": 0.5,
                    "maximumResolution": 999,
                    "area": "qa",
                    "locationWkt": "POINT (1 1)"
                  }
                }
                """),
            200);

        var rule = await repository.GetByIdAsync("elastic-id");

        Assert.NotNull(rule);
        Assert.Equal("elastic-id", rule.Id);
        Assert.Equal("one", rule.RuleName);
    }

    [Fact]
    public async Task GetAllHydratesIdsFromSearchHits()
    {
        byte[]? requestBody = null;
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "took": 1,
                  "timed_out": false,
                  "_shards": { "total": 1, "successful": 1, "skipped": 0, "failed": 0 },
                  "hits": {
                    "total": { "value": 1, "relation": "eq" },
                    "max_score": 1.0,
                    "hits": [
                      {
                        "_index": "rules",
                        "_type": "_doc",
                        "_id": "elastic-id",
                        "_score": 1.0,
                        "_source": {
                          "ruleName": "one",
                          "algorithmName": "FindAir",
                          "sensors": {},
                          "isActive": true,
                          "tenantsInfo": [],
                          "minimumResolution": 0.5,
                          "maximumResolution": 999,
                          "area": "qa",
                          "locationWkt": "POINT (1 1)"
                        }
                      }
                    ]
                  }
                }
                """),
            200,
            call => requestBody = call.RequestBodyInBytes);

        var rules = await repository.GetAllAsync(isActive: null, from: 25, size: 50);

        var rule = Assert.Single(rules);
        Assert.Equal("elastic-id", rule.Id);
        Assert.NotNull(requestBody);
        using var request = JsonDocument.Parse(requestBody);
        Assert.Equal(25, request.RootElement.GetProperty("from").GetInt32());
        Assert.Equal(50, request.RootElement.GetProperty("size").GetInt32());
    }

    [Fact]
    public async Task GetNamesRequestsOnlyRuleNameFromElasticsearch()
    {
        byte[]? requestBody = null;
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "took": 1,
                  "timed_out": false,
                  "_shards": { "total": 1, "successful": 1, "skipped": 0, "failed": 0 },
                  "hits": {
                    "total": { "value": 1, "relation": "eq" },
                    "max_score": 1.0,
                    "hits": [
                      {
                        "_index": "rules",
                        "_type": "_doc",
                        "_id": "elastic-id",
                        "_score": 1.0,
                        "_source": {
                          "ruleName": "one"
                        }
                      }
                    ]
                  }
                }
                """),
            200,
            call => requestBody = call.RequestBodyInBytes);

        var names = await repository.GetNamesAsync(isActive: true, from: 10, size: 20);

        Assert.Equal(["one"], names);
        Assert.NotNull(requestBody);
        using var request = JsonDocument.Parse(requestBody);
        Assert.Equal(10, request.RootElement.GetProperty("from").GetInt32());
        Assert.Equal(20, request.RootElement.GetProperty("size").GetInt32());

        var sourceFields = request.RootElement
            .GetProperty("_source")
            .GetProperty("includes");
        var sourceField = Assert.Single(sourceFields.EnumerateArray());
        Assert.Equal("ruleName", sourceField.GetString());
    }

    [Fact]
    public async Task ExistsByNameExcludesCurrentIdInsideSizeOneQuery()
    {
        byte[]? requestBody = null;
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "took": 1,
                  "timed_out": false,
                  "_shards": { "total": 1, "successful": 1, "skipped": 0, "failed": 0 },
                  "hits": {
                    "total": { "value": 0, "relation": "eq" },
                    "max_score": null,
                    "hits": []
                  }
                }
                """),
            200,
            call => requestBody = call.RequestBodyInBytes);

        var exists = await repository.ExistsByNameAsync("one", excludingId: "rule-1");

        Assert.False(exists);
        Assert.NotNull(requestBody);
        using var request = JsonDocument.Parse(requestBody);
        Assert.Equal(1, request.RootElement.GetProperty("size").GetInt32());

        var queryJson = request.RootElement.GetProperty("query").GetRawText();
        Assert.Contains("\"ruleName.keyword\"", queryJson, StringComparison.Ordinal);
        Assert.Contains("\"must_not\"", queryJson, StringComparison.Ordinal);
        Assert.Contains("\"ids\"", queryJson, StringComparison.Ordinal);
        Assert.Contains("\"rule-1\"", queryJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetByIdThrowsRepositoryExceptionForDependencyFailure()
    {
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "error": {
                    "type": "unavailable_shards_exception",
                    "reason": "Elasticsearch unavailable"
                  },
                  "status": 503
                }
                """),
            503);

        await Assert.ThrowsAsync<RuleRepositoryException>(() =>
            repository.GetByIdAsync("rule-1"));
    }

    [Fact]
    public async Task GetByIdReturnsNullForConfirmedMissingDocument()
    {
        var repository = CreateRepository(
            Encoding.UTF8.GetBytes("""
                {
                  "_index": "rules",
                  "_type": "_doc",
                  "_id": "missing",
                  "found": false
                }
                """),
            404);

        var rule = await repository.GetByIdAsync("missing");

        Assert.Null(rule);
    }

    private static ElasticsearchRuleRepository CreateRepository(
        byte[] responseBody,
        int statusCode,
        Action<IApiCallDetails>? onRequestCompleted = null)
    {
        var connection = new InMemoryConnection(responseBody, statusCode);
        var settings = new ConnectionSettings(
                new SingleNodeConnectionPool(new Uri("http://localhost:9200")),
                connection,
                sourceSerializer: (_, _) => new SystemTextJsonSourceSerializer())
            .DefaultIndex("rules")
            .DisableDirectStreaming();

        if (onRequestCompleted is not null)
        {
            settings = settings.OnRequestCompleted(onRequestCompleted);
        }

        return new ElasticsearchRuleRepository(
            new ElasticsearchDocumentClient(new ElasticClient(settings)),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }));
    }

    private static RuleDto ValidRule(string id) =>
        new()
        {
            Id = id,
            RuleName = "one",
            AlgorithmName = AlgorithmName.FindAir,
            MinimumResolution = 0.5,
            MaximumResolution = 1,
            LocationWkt = "POINT (1 1)",
            TenantsInfo =
            [
                new TenantInfo
                {
                    TenantId = "tenant-1",
                    TilingConfigs =
                    [
                        new TilingConfig
                        {
                            TileSizeWidth = 512,
                            TileSizeHeight = 512
                        }
                    ]
                }
            ]
        };
}
