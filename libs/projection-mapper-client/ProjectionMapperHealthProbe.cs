using Microsoft.Extensions.Logging;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperHealthProbe : IProjectionMapperHealthProbe
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProjectionMapperHealthProbe> _logger;

    public ProjectionMapperHealthProbe(HttpClient httpClient, ILogger<ProjectionMapperHealthProbe> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<bool> IsHealthyAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogDebug("Starting projection mapper health probe.");

        try
        {
            using var response = await _httpClient.GetAsync("/health", cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Projection mapper health probe succeeded.");
                return true;
            }

            _logger.LogWarning(
                "Projection mapper health probe returned {StatusCode}.",
                (int)response.StatusCode);
            return false;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "Projection mapper health probe failed.");
            return false;
        }
    }
}
