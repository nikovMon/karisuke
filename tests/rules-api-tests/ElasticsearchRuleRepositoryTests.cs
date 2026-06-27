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
    public async Task SaveUsesThePublicRuleDocumentFieldNames()
    {
        byte[]? requestBody = null;
        var responseBody = Encoding.UTF8.GetBytes("""
            {
              "_index": "rules",
              "_type": "_doc",
              "_id": "rule-1",
              "_version": 1,
              "result": "created",
              "_shards": { "total": 1, "successful": 1, "failed": 0 },
              "_seq_no": 0,
              "_primary_term": 1
            }
            """);
        var connection = new InMemoryConnection(responseBody, 201);
        var settings = new ConnectionSettings(
                new SingleNodeConnectionPool(new Uri("http://localhost:9200")),
                connection,
                sourceSerializer: (_, _) => new SystemTextJsonSourceSerializer())
            .DefaultIndex("rules")
            .DisableDirectStreaming()
            .OnRequestCompleted(call => requestBody = call.RequestBodyInBytes);
        var repository = new ElasticsearchRuleRepository(
            new ElasticClient(settings),
            Options.Create(new RulesElasticsearchOptions { IndexName = "rules" }));

        await repository.SaveAsync(new RuleConfigDto
        {
            Id = "rule-1",
            RuleName = "one",
            AlgorithmName = AlgorithmName.Finder,
            MinResolution = 0.5,
            Wkt = "POINT (1 1)"
        });

        Assert.NotNull(requestBody);
        using var document = JsonDocument.Parse(requestBody);
        Assert.Equal("rule-1", document.RootElement.GetProperty("_id").GetString());
        Assert.False(document.RootElement.TryGetProperty("id", out _));
        Assert.Equal("Finder", document.RootElement.GetProperty("algorithmName").GetString());
        Assert.Equal(999, document.RootElement.GetProperty("maxResolution").GetDouble());
    }
}
