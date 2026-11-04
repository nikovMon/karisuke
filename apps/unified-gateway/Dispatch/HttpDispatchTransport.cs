using System.Collections.Frozen;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqClient;

namespace ImagingPipeline.UnifiedGateway.Dispatch;

/// <summary>
/// Sends each unit as one HTTP request to the endpoint its pipeline declares in the catalog, inside
/// the message handler. The catalog timeout bounds the whole request, including connection setup.
/// </summary>
public sealed class HttpDispatchTransport : IDispatchTransport
{
    public const string HttpClientName = "unified-gateway.dispatch";
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    private readonly IHttpClientFactory _clients;
    private readonly FrozenDictionary<string, HttpTransportOptions> _endpoints;

    public HttpDispatchTransport(IPipelineCatalog catalog, IHttpClientFactory clients)
    {
        _clients = clients;
        _endpoints = catalog.GetEnabled()
            .Where(pipeline => pipeline.Transport.Kind == PipelineTransportKind.Http)
            .ToFrozenDictionary(pipeline => pipeline.PipelineId, pipeline => pipeline.Transport.Http!, StringComparer.Ordinal);
    }

    public PipelineTransportKind Kind => PipelineTransportKind.Http;

    public async Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (!_endpoints.TryGetValue(unit.PipelineId, out var endpoint))
        {
            // Only an enabled pipeline whose catalog transport is http has an endpoint.
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Handler, failureDetails:
                Failure(unit, RabbitMqFailureKind.Configuration, "gateway.http.endpoint_missing", "No enabled HTTP endpoint is configured."));
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
        try
        {
            using var request = CreateRequest(unit, endpoint);
            using var response = await _clients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return Classify(unit, response.StatusCode, endpoint.DeliveredStatusCodes);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException ex)
        {
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout, ex, failureDetails:
                Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.timeout", "HTTP dispatch timed out."));
        }
        catch (HttpRequestException ex)
        {
            var failure = ex.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError =>
                    Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.connection", "HTTP connection failed."),
                HttpRequestError.SecureConnectionError or HttpRequestError.UserAuthenticationError =>
                    Failure(unit, RabbitMqFailureKind.Configuration, "gateway.http.authentication", "HTTP authentication or TLS setup failed."),
                _ when ex.InnerException is SocketException =>
                    Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.connection", "HTTP connection failed."),
                _ => Failure(unit, RabbitMqFailureKind.Unknown, "gateway.http.unclassified", "HTTP dispatch failed without a recognized cause.")
            };
            return DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Connection, ex, failureDetails: failure);
        }
        catch (InvalidDispatchHeaderException ex)
        {
            return DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, exception: ex, failureDetails:
                Failure(unit, RabbitMqFailureKind.Configuration, "gateway.http.invalid_header", "An HTTP dispatch header is invalid."));
        }
    }

    internal static HttpRequestMessage CreateRequest(DispatchUnit unit, HttpTransportOptions endpoint)
    {
        var payload = unit.Work.Payload;
        var content = new ByteArrayContent(payload.Body);
        content.Headers.ContentType = MediaTypeHeaderValue.Parse(payload.ContentType);
        var request = new HttpRequestMessage(new HttpMethod(endpoint.Method), endpoint.Endpoint) { Content = content };

        // Resolve precedence before choosing the correct request or content header collection.
        var headers = new Dictionary<string, string>(endpoint.Headers, StringComparer.OrdinalIgnoreCase);
        foreach (var attribute in payload.Attributes)
        {
            headers[attribute.Key] = attribute.Value;
        }
        headers[IdempotencyKeyHeader] = unit.DispatchId;

        try
        {
            foreach (var header in headers)
            {
                if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value) &&
                    !content.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    throw new InvalidDispatchHeaderException();
                }
            }
            return request;
        }
        catch
        {
            request.Dispose();
            throw;
        }
    }

    private sealed class InvalidDispatchHeaderException()
        : Exception("An HTTP dispatch header could not be added to the request or its content.")
    {
    }

    internal static DispatchOutcome Classify(DispatchUnit unit, HttpStatusCode statusCode, IReadOnlyCollection<int> deliveredStatusCodes)
    {
        var code = (int)statusCode;
        return code switch
        {
            >= 200 and < 300 => DispatchOutcome.Delivered(unit, code),
            _ when deliveredStatusCodes.Contains(code) => DispatchOutcome.Delivered(unit, code),
            408 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout, statusCode: code, failureDetails:
                Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.timeout", "HTTP dependency reported a timeout.", code)),
            429 or 503 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Unavailable, statusCode: code, failureDetails:
                Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.unavailable", "HTTP dependency is unavailable or rate limited.", code)),
            >= 500 and <= 599 => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Dependency, statusCode: code, failureDetails:
                Failure(unit, RabbitMqFailureKind.Transient, "gateway.http.retryable_status", "HTTP dependency returned a retryable status.", code)),
            >= 300 and <= 399 or 401 or 403 or 404 or 405 or 407 =>
                DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, statusCode: code, failureDetails:
                    Failure(unit, RabbitMqFailureKind.Configuration, "gateway.http.configuration", "HTTP dependency rejected the configured request.", code)),
            // A dependency rejection alone does not prove that the original source data is invalid.
            >= 400 and <= 499 => DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, statusCode: code, failureDetails:
                Failure(unit, RabbitMqFailureKind.DependencyResponse, "gateway.http.rejected_response", "HTTP dependency rejected the dispatch.", code)),
            _ => DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, statusCode: code, failureDetails:
                Failure(unit, RabbitMqFailureKind.Unknown, "gateway.http.unknown_status", "HTTP dependency returned an unrecognized status.", code))
        };
    }

    private static RabbitMqFailure Failure(DispatchUnit unit, RabbitMqFailureKind kind, string code, string message, int? statusCode = null) =>
        new(kind, code, message, unit.PipelineId, "dispatch_http", statusCode);
}
