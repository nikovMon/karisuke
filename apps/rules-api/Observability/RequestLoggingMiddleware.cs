using System.Diagnostics;

namespace ImagingPipeline.Rules.Api.Observability;

public sealed class RequestLoggingMiddleware
{
    private static readonly EventId RequestStartedEvent = new(0, "HttpRequestStarted");
    private static readonly EventId RequestCompletedEvent = new(0, "HttpRequestCompleted");
    private readonly RequestDelegate _next;
    private readonly ILogger<RequestLoggingMiddleware> _logger;

    public RequestLoggingMiddleware(
        RequestDelegate next,
        ILogger<RequestLoggingMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TraceId"] = context.TraceIdentifier,
            ["RequestMethod"] = context.Request.Method,
            ["RequestPath"] = context.Request.Path.Value
        });

        _logger.LogDebug(
            RequestStartedEvent,
            "HTTP {RequestMethod} {RequestPath} started. TraceId: {TraceId}",
            context.Request.Method,
            context.Request.Path.Value,
            context.TraceIdentifier);

        var started = Stopwatch.GetTimestamp();
        await _next(context);

        var elapsedMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var endpoint = context.GetEndpoint()?.DisplayName ?? "unmatched";
        var routeId = context.Request.RouteValues.TryGetValue("id", out var id)
            ? Convert.ToString(id, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var level = context.Response.StatusCode switch
        {
            >= StatusCodes.Status500InternalServerError => LogLevel.Error,
            >= StatusCodes.Status400BadRequest => LogLevel.Warning,
            _ => LogLevel.Information
        };

        _logger.Log(
            level,
            RequestCompletedEvent,
            "HTTP {RequestMethod} {RequestPath} completed with {StatusCode} in {ElapsedMs:F2} ms. Endpoint: {Endpoint}; RouteId: {RouteId}; TraceId: {TraceId}",
            context.Request.Method,
            context.Request.Path.Value,
            context.Response.StatusCode,
            elapsedMs,
            endpoint,
            routeId,
            context.TraceIdentifier);
    }
}
