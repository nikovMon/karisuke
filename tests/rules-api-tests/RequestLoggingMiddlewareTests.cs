using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class RequestLoggingMiddlewareTests
{
    [Theory]
    [InlineData(StatusCodes.Status200OK, LogLevel.Information)]
    [InlineData(StatusCodes.Status400BadRequest, LogLevel.Warning)]
    [InlineData(StatusCodes.Status503ServiceUnavailable, LogLevel.Error)]
    public async Task LogsEveryCompletedRequestAtTheRelevantLevel(
        int statusCode,
        LogLevel expectedLevel)
    {
        var logger = new RecordingLogger<RequestLoggingMiddleware>();
        var middleware = new RequestLoggingMiddleware(
            context =>
            {
                context.Response.StatusCode = statusCode;
                context.Request.RouteValues["id"] = "rule-1";
                return Task.CompletedTask;
            },
            logger);
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "trace-123"
        };
        context.Request.Method = HttpMethods.Patch;
        context.Request.Path = "/rules/rule-1";

        await middleware.InvokeAsync(context);

        var started = Assert.Single(logger.Entries, entry => entry.EventId.Name == "HttpRequestStarted");
        Assert.Equal(LogLevel.Debug, started.Level);
        Assert.Equal(0, started.EventId.Id);
        Assert.Equal("trace-123", started.Properties["TraceId"]);

        var entry = Assert.Single(logger.Entries, item => item.EventId.Name == "HttpRequestCompleted");
        Assert.Equal(expectedLevel, entry.Level);
        Assert.Equal(0, entry.EventId.Id);
        Assert.Equal("HttpRequestCompleted", entry.EventId.Name);
        Assert.Equal(statusCode, entry.Properties["StatusCode"]);
        Assert.Equal("rule-1", entry.Properties["RouteId"]);
        Assert.Equal("trace-123", entry.Properties["TraceId"]);
        var scope = Assert.Single(logger.Scopes);
        Assert.Equal("PATCH", scope["RequestMethod"]);
        Assert.Equal("/rules/rule-1", scope["RequestPath"]);
        Assert.Equal("trace-123", scope["TraceId"]);
    }
}
