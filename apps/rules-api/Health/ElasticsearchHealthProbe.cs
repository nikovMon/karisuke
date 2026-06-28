using Nest;

namespace ImagingPipeline.Rules.Api.Health;

public sealed class ElasticsearchHealthProbe : IElasticsearchHealthProbe
{
    private readonly IElasticClient _client;
    private readonly ILogger<ElasticsearchHealthProbe> _logger;

    public ElasticsearchHealthProbe(
        IElasticClient client,
        ILogger<ElasticsearchHealthProbe> logger)
    {
        _client = client;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting Elasticsearch health probe.");

        try
        {
            var response = await _client.PingAsync(descriptor => descriptor, cancellationToken);
            if (response.IsValid)
            {
                _logger.LogDebug(
                    "Elasticsearch health probe succeeded. HttpStatusCode: {HttpStatusCode}",
                    response.ApiCall?.HttpStatusCode);
                return true;
            }

            _logger.LogWarning(
                "Elasticsearch health probe returned an invalid response. HttpStatusCode: {HttpStatusCode}; FailureType: {FailureType}",
                response.ApiCall?.HttpStatusCode,
                response.OriginalException?.GetType().Name);
            return false;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Elasticsearch health probe failed.");
            return false;
        }
    }
}
