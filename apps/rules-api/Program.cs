using System.Diagnostics;
using System.Text.Json.Serialization;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ImagingPipeline.Rules.Api;

public sealed partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddImagingPipelineObservability(
            ObservabilityServiceNames.RulesApi,
            instrumentAspNetCore: true);
        builder.Services.AddControllers(options => options.Filters.Add<RulesOperationFilter>())
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            });
        builder.Services.Configure<ApiBehaviorOptions>(options =>
        {
            options.InvalidModelStateResponseFactory = context =>
            {
                var logger = context.HttpContext.RequestServices
                    .GetRequiredService<ILogger<Program>>();

                var invalidFields = context.ModelState
                    .Where(item => item.Value?.Errors.Count > 0)
                    .Select(item => item.Key)
                    .ToArray();

                var fieldsToLog = invalidFields
                    .Take(100)
                    .ToArray();

                var totalInvalidFieldsCount = invalidFields.Length;

                var totalErrorsCount = context.ModelState.Values
                    .Sum(value => value.Errors.Count);

                var operation = RulesOperationFilter.ResolveRequestOperation(context.HttpContext.Request);
                RulesTelemetry.RecordValidationFailure(operation);
                if (Activity.Current?.IsAllDataRequested == true)
                {
                    Activity.Current.SetTag(TelemetryAttributeNames.PipelineOutcome, "rejected");
                    Activity.Current.SetTag(TelemetryAttributeNames.ErrorCategory, "validation");
                }
                LogRequestModelValidationFailed(
                    logger,
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value,
                    totalInvalidFieldsCount,
                    totalErrorsCount,
                    fieldsToLog.Length,
                    string.Join(", ", fieldsToLog));

                var problemDetailsFactory = context.HttpContext.RequestServices
                    .GetRequiredService<ProblemDetailsFactory>();
                var problemDetails = problemDetailsFactory.CreateValidationProblemDetails(
                    context.HttpContext,
                    context.ModelState,
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "One or more validation errors occurred.");
                return new BadRequestObjectResult(problemDetails);
            };
        });
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new()
            {
                Title = "Rules API",
                Version = "v1",
                Description = "API for managing Elasticsearch-backed rule configuration documents."
            });
        });
        builder.Services.AddElasticsearchClient(builder.Configuration);
        builder.Services.AddOptions<RulesElasticsearchOptions>()
            .Bind(builder.Configuration.GetSection(RulesElasticsearchOptions.SectionName))
            .Validate(options => options.IsValid(out _), "Rules Elasticsearch settings are invalid.")
            .ValidateOnStart();
        builder.Services.AddSingleton<RuleService>();
        builder.Services.AddSingleton<IElasticsearchHealthProbe, ElasticsearchHealthProbe>();

        var app = builder.Build();

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                if (exception is RulePersistenceException persistenceException)
                {
                    Activity.Current.SetTelemetryError(
                        TelemetryErrorCategory.Dependency,
                        persistenceException,
                        recordException: false);
                    LogPersistenceFailure(
                        app.Logger,
                        persistenceException,
                        context.Request.Method,
                        context.Request.Path.Value);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Elasticsearch dependency is unavailable." });
                    return;
                }

                Activity.Current.SetTelemetryError(
                    TelemetryErrorCategory.Handler,
                    exception,
                    recordException: false);
                LogUnexpectedServerError(
                    app.Logger,
                    exception,
                    context.Request.Method,
                    context.Request.Path.Value);
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Unexpected server error." });
            });
        });

        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();
        app.MapGet(
            "/health",
            async (IElasticsearchHealthProbe healthProbe, CancellationToken cancellationToken) =>
            {
                var isHealthy = await healthProbe.IsHealthyAsync(cancellationToken);
                return isHealthy
                    ? Results.Ok(new { status = "Healthy" })
                    : Results.Json(
                        new { status = "Unhealthy" },
                        statusCode: StatusCodes.Status503ServiceUnavailable);
            });

        await app.RunAsync();
    }

    [LoggerMessage(
        EventId = 1001,
        Level = LogLevel.Warning,
        Message = "Request model validation failed for {RequestMethod} {RequestPath}. Total invalid fields: {TotalInvalidFieldsCount}. Total errors: {TotalErrorsCount}. Showing first {ShownFieldsCount} fields: {Fields}")]
    private static partial void LogRequestModelValidationFailed(
        ILogger logger,
        string requestMethod,
        string? requestPath,
        int totalInvalidFieldsCount,
        int totalErrorsCount,
        int shownFieldsCount,
        string fields);

    [LoggerMessage(
        EventId = 1002,
        Level = LogLevel.Error,
        Message = "Elasticsearch persistence operation failed for {RequestMethod} {RequestPath}")]
    private static partial void LogPersistenceFailure(
        ILogger logger,
        Exception exception,
        string requestMethod,
        string? requestPath);

    [LoggerMessage(
        EventId = 1003,
        Level = LogLevel.Error,
        Message = "Unexpected server error for {RequestMethod} {RequestPath}")]
    private static partial void LogUnexpectedServerError(
        ILogger logger,
        Exception? exception,
        string requestMethod,
        string? requestPath);
}
