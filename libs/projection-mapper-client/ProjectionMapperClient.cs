using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.ProjectionMapperClient;

public sealed class ProjectionMapperClient : IProjectionMapperClient
{
    private readonly HttpClient _httpClient;
    private readonly ProjectionMapperOptions _options;

    public ProjectionMapperClient(
        HttpClient httpClient,
        IOptions<ProjectionMapperOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public Task<IReadOnlyList<IReadOnlyList<double>>> MapAsync(
        string overlayId,
        ProjectionMapperRequestDto request,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayId);
        ArgumentNullException.ThrowIfNull(request);

        return ExecuteAsync(
            overlayId,
            ProjectionMapperEndpointKeys.G2IMultiPoints,
            DependencyOperation.GroundToImage,
            PipelineItem.GroundPoint,
            request.Coordinates.Count,
            request,
            cancellationToken);
    }

    public Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayId);
        ArgumentNullException.ThrowIfNull(coordinates);

        return ExecuteAsync(
            overlayId,
            ProjectionMapperEndpointKeys.I2GById,
            DependencyOperation.ImageToGround,
            PipelineItem.Coordinate,
            coordinates.Count,
            new I2GByIdRequestDto { Coordinates = coordinates },
            cancellationToken);
    }

    public Task<IReadOnlyList<IReadOnlyList<double>>> ProcessBatchByRegistrationAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        string gridType,
        string? gridUri,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(overlayId);
        ArgumentNullException.ThrowIfNull(coordinates);
        ArgumentException.ThrowIfNullOrWhiteSpace(gridType);

        return ExecuteByRegistrationAsync(
            overlayId,
            coordinates,
            gridType,
            gridUri,
            cancellationToken);
    }

    private async Task<IReadOnlyList<IReadOnlyList<double>>> ExecuteAsync<TRequest>(
        string overlayId,
        string endpointKey,
        DependencyOperation operation,
        PipelineItem batchItem,
        int batchSize,
        TRequest request,
        CancellationToken cancellationToken,
        bool includeQueryParams = true)
    {
        var spanName = operation == DependencyOperation.GroundToImage
            ? "projection_mapper ground_to_image"
            : "projection_mapper image_to_ground";
        using var activity = TelemetrySources.ProjectionMapper.StartActivity(spanName, ActivityKind.Internal);
        activity.AddPipelineContext(imageId: overlayId);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.DependencyName, "projection_mapper");
            activity.SetTag(
                TelemetryAttributeNames.DependencyOperation,
                operation == DependencyOperation.GroundToImage ? "ground_to_image" : "image_to_ground");
            activity.SetTag("projection_mapper.use_cache", _options.UseCache);
            activity.SetTag("projection_mapper.batch.size", batchSize);
        }

        DependencyTelemetry.RecordBatchSize(
            DependencyName.ProjectionMapper,
            operation,
            batchItem,
            batchSize);

        var started = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Success;
        var error = TelemetryErrorCategory.None;

        try
        {
            if (!_options.Endpoints.TryGetValue(endpointKey, out var endpoint) ||
                string.IsNullOrWhiteSpace(endpoint))
            {
                error = TelemetryErrorCategory.Validation;
                throw new ProjectionMapperClientException(
                    $"ProjectionMapper endpoint '{endpointKey}' is not configured.");
            }

            var requestUri = includeQueryParams
                ? $"{endpoint}?overlayId={Uri.EscapeDataString(overlayId)}&useCache={(_options.UseCache ? "true" : "false")}"
                : endpoint;
            using var requestMessage = new HttpRequestMessage(HttpMethod.Post, requestUri)
            {
                Content = JsonContent.Create(request),
                Headers =
                {
                    { "sendingSystem", _options.SendingSystem }
                }
            };
            RecordKnownContentLength(operation, PipelineDirection.Egress, requestMessage.Content);
            using var response = await _httpClient.SendAsync(requestMessage, cancellationToken);
            RecordKnownContentLength(operation, PipelineDirection.Ingress, response.Content);
            if (!response.IsSuccessStatusCode)
            {
                error = ClassifyStatusCode(response.StatusCode);
                throw new ProjectionMapperClientException(
                    $"Projection mapper returned HTTP {(int)response.StatusCode} for endpoint '{endpointKey}'.");
            }

            var payload = await response.Content.ReadFromJsonAsync<ProjectionMapperResponseDto>(cancellationToken);
            if (payload is null)
            {
                error = TelemetryErrorCategory.Serialization;
                throw new ProjectionMapperClientException("Projection mapper returned an empty response.");
            }

            activity.SetTelemetrySuccess();
            return payload.Coordinates;
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            activity.SetTelemetryError(error, ex, recordException: false);
            throw;
        }
        catch (OperationCanceledException ex)
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Timeout;
            var wrapped = new ProjectionMapperClientException("Projection mapper request timed out.", ex);
            activity.SetTelemetryError(error, wrapped);
            throw wrapped;
        }
        catch (HttpRequestException ex)
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Unavailable;
            var wrapped = new ProjectionMapperClientException("Projection mapper request failed.", ex);
            activity.SetTelemetryError(error, wrapped);
            throw wrapped;
        }
        catch (JsonException ex)
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Serialization;
            var wrapped = new ProjectionMapperClientException(
                "Projection mapper response could not be deserialized.",
                ex);
            activity.SetTelemetryError(error, wrapped);
            throw wrapped;
        }
        catch (NotSupportedException ex)
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Serialization;
            var wrapped = new ProjectionMapperClientException(
                "Projection mapper payload could not be serialized or deserialized.",
                ex);
            activity.SetTelemetryError(error, wrapped);
            throw wrapped;
        }
        catch (ProjectionMapperClientException ex)
        {
            outcome = TelemetryOutcome.Failure;
            if (error == TelemetryErrorCategory.None)
            {
                error = TelemetryErrorCategory.Dependency;
            }

            activity.SetTelemetryError(error, ex);
            throw;
        }
        catch (Exception ex)
        {
            outcome = TelemetryOutcome.Failure;
            error = TelemetryErrorCategory.Unknown;
            var wrapped = new ProjectionMapperClientException("Projection mapper operation failed.", ex);
            activity.SetTelemetryError(error, wrapped);
            throw wrapped;
        }
        finally
        {
            DependencyTelemetry.RecordOperation(
                DependencyName.ProjectionMapper,
                operation,
                TelemetryTiming.ElapsedSeconds(started),
                outcome,
                error);
        }
    }

    private async Task<IReadOnlyList<IReadOnlyList<double>>> ExecuteByRegistrationAsync(
        string overlayId,
        IReadOnlyList<IReadOnlyList<double>> coordinates,
        string gridType,
        string? gridUri,
        CancellationToken cancellationToken)
    {
        var request = new I2GByRegistrationRequestDto
        {
            OverlayId = overlayId,
            ReturnAltitude = false,
            PixelPoints = coordinates,
            GridType = gridType,
            GridUri = gridUri,
            UseCache = _options.UseCache
        };

        return await ExecuteAsync(
            overlayId,
            ProjectionMapperEndpointKeys.I2GByRegistration,
            DependencyOperation.ImageToGround,
            PipelineItem.Coordinate,
            coordinates.Count,
            request,
            cancellationToken,
            includeQueryParams: false);
    }

    private static TelemetryErrorCategory ClassifyStatusCode(HttpStatusCode statusCode)
    {
        if (statusCode == HttpStatusCode.RequestTimeout)
        {
            return TelemetryErrorCategory.Timeout;
        }

        return statusCode == HttpStatusCode.TooManyRequests || (int)statusCode >= 500
            ? TelemetryErrorCategory.Unavailable
            : TelemetryErrorCategory.Dependency;
    }

    private static void RecordKnownContentLength(
        DependencyOperation operation,
        PipelineDirection direction,
        HttpContent content)
    {
        if (content.Headers.ContentLength is { } contentLength && contentLength >= 0)
        {
            DependencyTelemetry.RecordPayloadSize(
                DependencyName.ProjectionMapper,
                operation,
                direction,
                contentLength);
        }
    }
}
