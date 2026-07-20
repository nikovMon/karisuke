using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperClient : IProjectionMapperClient
{
    private readonly HttpClient _httpClient;
    private readonly ProjectionMapperOptions _options;
    private readonly ILogger<ProjectionMapperClient> _logger;

    public ProjectionMapperClient(
        HttpClient httpClient,
        IOptions<ProjectionMapperOptions> options,
        ILogger<ProjectionMapperClient> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(
        string overlayId,
        ProjectionMapperRequestDto request,
        CancellationToken cancellationToken = default)
    {
        if (!_options.Endpoints.TryGetValue(ProjectionMapperEndpointKeys.G2IMultiPoints, out var endpoint))
        {
            throw new ProjectionMapperClientException(
                $"ProjectionMapper endpoint '{ProjectionMapperEndpointKeys.G2IMultiPoints}' is not configured.");
        }

        var requestUri = $"{endpoint}?overlayId={Uri.EscapeDataString(overlayId)}&useCache={(_options.UseCache ? "true" : "false")}";

        HttpResponseMessage response;
        try
        {
            response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Projection mapper call failed for overlay {OverlayId}.", overlayId);
            throw new ProjectionMapperClientException("Projection mapper request failed.", ex);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Projection mapper returned {StatusCode} for overlay {OverlayId}.",
                    (int)response.StatusCode,
                    overlayId);
                throw new ProjectionMapperClientException(
                    $"Projection mapper returned HTTP {(int)response.StatusCode}.");
            }

            var payload = await response.Content.ReadFromJsonAsync<ProjectionMapperResponseDto>(cancellationToken);
            if (payload is null)
            {
                _logger.LogWarning("Projection mapper returned an empty response for overlay {OverlayId}.", overlayId);
                throw new ProjectionMapperClientException("Projection mapper returned an empty response.");
            }

            return payload.Coordinates;
        }
    }
}
