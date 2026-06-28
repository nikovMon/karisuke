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
        Assert.Equal("Finder", document.RootElement.GetProperty("algorithmName").GetString());
        Assert.Equal(999, document.RootElement.GetProperty("maxResolution").GetDouble());
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
                    "algorithmName": "Finder",
                    "sensors": {},
                    "isActive": true,
                    "tenants": [],
                    "minResolution": 0.5,
                    "maxResolution": 999,
                    "area": "qa",
                    "wkt": "POINT (1 1)"
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
                          "algorithmName": "Finder",
                          "sensors": {},
                          "isActive": true,
                          "tenants": [],
                          "minResolution": 0.5,
                          "maxResolution": 999,
                          "area": "qa",
                          "wkt": "POINT (1 1)"
                        }
                      }
                    ]
                  }
                }
                """),
            200);

        var rules = await repository.GetAllAsync(isActive: null);

        var rule = Assert.Single(rules);
        Assert.Equal("elastic-id", rule.Id);
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
            new ElasticClient(settings),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }));
    }

    private static RuleConfigDto ValidRule(string id) =>
        new()
        {
            Id = id,
            RuleName = "one",
            AlgorithmName = AlgorithmName.Finder,
            MinResolution = 0.5,
            Wkt = "POINT (1 1)"
        };
}
