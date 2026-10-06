using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Sends each unit as one HTTP request to the endpoint its pipeline declares in the catalog, inside
/// the message handler. The catalog timeout bounds the whole request, including connection setup.
/// </summary>
public sealed class HttpDispatchTransport : IDispatchTransport
{
    public const string TransportKind = "http";
    public const string HttpClientName = "unified-gateway.dispatch";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly IHttpClientFactory _clients;
    private readonly FrozenDictionary<string, HttpTransportOptions> _endpoints;

    public HttpDispatchTransport(IPipelineCatalog catalog, IHttpClientFactory clients)
    {
        _clients = clients;
        _endpoints = catalog.GetEnabled()
            .Where(pipeline => pipeline.Transport.Kind == TransportKind)
            .ToFrozenDictionary(pipeline => pipeline.PipelineId, pipeline => pipeline.Transport.Http!, StringComparer.Ordinal);
    }

    public string Kind => TransportKind;

    public async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_endpoints.TryGetValue(unit.PipelineId, out var endpoint))
        {
            // Only an enabled pipeline whose catalog transport is http has an endpoint.
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Handler);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
        try
        {
            using var request = CreateRequest(unit, endpoint);
            using var response = await _clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return Classify(unit, response.StatusCode);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout, ex);
        }
        catch (HttpRequestException ex)
        {
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Connection, ex);
        }
    }

    internal static HttpRequestMessage CreateRequest(DispatchUnit unit, HttpTransportOptions endpoint)
    {
        var payload = unit.Work.Payload;
        var content = new ByteArrayContent(payload.Body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType);
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Endpoint) { Content = content };

        // Configured headers first; the contract's attributes and the idempotency key win on a name clash.
        foreach (var header in endpoint.Headers)
        {
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        foreach (var attribute in payload.Attributes)
        {
            request.Headers.Remove(attribute.Key);
            request.Headers.TryAddWithoutValidation(attribute.Key, attribute.Value);
        }

        request.Headers.Remove(IdempotencyKeyHeader);
        request.Headers.TryAddWithoutValidation(IdempotencyKeyHeader, unit.DispatchId);
        return request;
    }

    internal static DispatchOutcome Classify(DispatchUnit unit, HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code switch
        {
            >= 200 and < 300 => DispatchOutcome.Delivered(unit, code),
            408 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout, statusCode: code),
            429 or 503 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Unavailable, statusCode: code),
            >= 500 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Dependency, statusCode: code),
            // The endpoint refused the request as sent. Redirects are not followed, so a 3xx means
            // the configured endpoint is wrong.
            _ => DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, statusCode: code)
        };
    }
}
