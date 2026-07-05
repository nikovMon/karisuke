using Elasticsearch.Net;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Nest;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class ElasticsearchHealthProbeTests
{
    [Fact]
    public async Task InvalidPingReturnsFalseAndLogsWarning()
    {
        var connection = new InMemoryConnection([], statusCode: 503);
        var settings = new ConnectionSettings(
            new SingleNodeConnectionPool(new Uri("http://localhost:9200")),
            connection);
        var logger = new RecordingLogger<ElasticsearchHealthProbe>();
        var probe = new ElasticsearchHealthProbe(new ElasticClient(settings), logger);

        var isHealthy = await probe.IsHealthyAsync();

        Assert.False(isHealthy);
        var debugEntry = Assert.Single(logger.Entries, entry => entry.Level == Microsoft.Extensions.Logging.LogLevel.Debug);
        Assert.Contains("Starting", debugEntry.Message, StringComparison.Ordinal);
        var entry = Assert.Single(logger.Entries, item => item.Level == Microsoft.Extensions.Logging.LogLevel.Warning);
        Assert.Equal(Microsoft.Extensions.Logging.LogLevel.Warning, entry.Level);
        Assert.Contains("invalid response", entry.Message, StringComparison.Ordinal);
        Assert.Equal(503, entry.Properties["HttpStatusCode"]);
    }
}
