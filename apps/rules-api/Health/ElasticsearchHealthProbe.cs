using Nest;

namespace ImagingPipeline.Rules.Api.Health;

public sealed class ElasticsearchHealthProbe : IElasticsearchHealthProbe
{
    private readonly IElasticClient _client;

    public ElasticsearchHealthProbe(IElasticClient client)
    {
        _client = client;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.PingAsync(descriptor => descriptor, cancellationToken);
            return response.IsValid;
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }
}
