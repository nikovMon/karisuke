using System.Diagnostics;
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
        using var activity = ProjectionMapperClientDiagnostics.ActivitySource.StartActivity(
            "projection-mapper map", ActivityKind.Client);
        activity?.SetTag("projection_mapper.overlay_id", overlayId);

        if (!_options.Endpoints.TryGetValue(ProjectionMapperEndpointKeys.G2IMultiPoints, out var endpoint))
        {
            throw new ProjectionMapperClientException(
                $"ProjectionMapper endpoint '{ProjectionMapperEndpointKeys.G2IMultiPoints}' is not configured.");
        }

        var requestUri = $"{endpoint}?overlayId={Uri.EscapeDataString(overlayId)}&useCache={(_options.UseCache ? "true" : "false")}";

        var started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProjectionMapperClientDiagnostics.Failures.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                _logger.LogError(ex, "Projection mapper call failed for overlay {OverlayId}.", overlayId);
                throw new ProjectionMapperClientException("Projection mapper request failed.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    ProjectionMapperClientDiagnostics.Failures.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
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
                    ProjectionMapperClientDiagnostics.Failures.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, "empty response");
                    _logger.LogWarning("Projection mapper returned an empty response for overlay {OverlayId}.", overlayId);
                    throw new ProjectionMapperClientException("Projection mapper returned an empty response.");
                }

                ProjectionMapperClientDiagnostics.Calls.Add(1);
                return payload.Coordinates;
            }
        }
        finally
        {
            ProjectionMapperClientDiagnostics.DurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("overlay_id", overlayId));
        }
    }

    public async Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        CancellationToken cancellationToken = default)
    {
        using var activity = ProjectionMapperClientDiagnostics.ActivitySource.StartActivity(
            "projection-mapper i2g-by-id", ActivityKind.Client);
        activity?.SetTag("projection_mapper.overlay_id", overlayId);
        activity?.SetTag("projection_mapper.coordinate_count", coordinates.Count);

        if (!_options.Endpoints.TryGetValue(ProjectionMapperEndpointKeys.I2GById, out var endpoint))
        {
            throw new ProjectionMapperClientException(
                $"ProjectionMapper endpoint '{ProjectionMapperEndpointKeys.I2GById}' is not configured.");
        }

        var requestUri = $"{endpoint}?overlayId={Uri.EscapeDataString(overlayId)}&useCache={(_options.UseCache ? "true" : "false")}";

        var request = new I2GByIdRequestDto { Coordinates = coordinates };

        var started = Stopwatch.GetTimestamp();
        try
        {
            HttpResponseMessage response;
            try
            {
                response = await _httpClient.PostAsJsonAsync(requestUri, request, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                ProjectionMapperClientDiagnostics.Failures.Add(1);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw new ProjectionMapperClientException("Projection mapper i2g-by-id request failed.", ex);
            }

            using (response)
            {
                if (!response.IsSuccessStatusCode)
                {
                    ProjectionMapperClientDiagnostics.Failures.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, $"HTTP {(int)response.StatusCode}");
                    throw new ProjectionMapperClientException(
                        $"Projection mapper i2g-by-id returned HTTP {(int)response.StatusCode}.");
                }

                var payload = await response.Content.ReadFromJsonAsync<ProjectionMapperResponseDto>(cancellationToken);
                if (payload is null)
                {
                    ProjectionMapperClientDiagnostics.Failures.Add(1);
                    activity?.SetStatus(ActivityStatusCode.Error, "empty response");
                    throw new ProjectionMapperClientException("Projection mapper i2g-by-id returned an empty response.");
                }

                ProjectionMapperClientDiagnostics.Calls.Add(1);
                return payload.Coordinates;
            }
        }
        finally
        {
            ProjectionMapperClientDiagnostics.DurationMs.Record(
                Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                new KeyValuePair<string, object?>("overlay_id", overlayId));
        }
    }
}
