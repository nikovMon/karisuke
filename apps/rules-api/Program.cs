using System.Diagnostics;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.AspNetCore.Routing;

namespace ImagingPipeline.Rules.Api;

public sealed class Program
{
    private const int MaxLoggedInvalidFields = 100;

    public static async Task Main(string[] args)
    {
        RegistrationQualityContract.EnsureValid();

        var builder = WebApplication.CreateBuilder(args);
        builder.Configuration.AddPipelineCatalogFileAsBase(builder.Environment.ContentRootPath);
        builder.AddImagingPipelineObservability(
            ObservabilityServiceNames.RulesApi,
            instrumentAspNetCore: true);
        builder.Services.AddControllers()
            .AddJsonOptions(options =>
            {
                options.JsonSerializerOptions.Converters.Add(new RegistrationQualityJsonConverter());
                options.JsonSerializerOptions.Converters.Add(new AlgorithmNameJsonConverter());
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

                var totalErrorsCount = context.ModelState.Values
                    .Sum(value => value.Errors.Count);

                var operation = RulesOperationFilter.ResolveRequestOperation(context.HttpContext.Request);
                RulesTelemetry.RecordValidationFailure(operation);
                if (Activity.Current?.IsAllDataRequested == true)
                {
                    Activity.Current.SetTag(TelemetryAttributeNames.PipelineOutcome, "rejected");
                    Activity.Current.SetTag(TelemetryAttributeNames.ErrorCategory, "validation");
                }
                using (BeginRequestScope(logger, context.HttpContext))
                using (logger.BeginScope(new KeyValuePair<string, object?>[]
                       {
                           new(TelemetryAttributeNames.RequestInvalidFieldNames, invalidFields.Take(MaxLoggedInvalidFields).ToArray()),
                           new(TelemetryAttributeNames.RequestInvalidFieldCount, invalidFields.Length),
                           new(TelemetryAttributeNames.RequestErrorCount, totalErrorsCount)
                       }))
                {
                    logger.RequestModelValidationFailed();
                }

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
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddSingleton<RuleAuditWriter>();
        builder.Services.AddSingleton<PipelineRuleService>();
        builder.Services.AddPipelineContracts();
        builder.Services.AddPipelineCatalog(builder.Configuration);
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
                    using (BeginRequestScope(app.Logger, context))
                    {
                        app.Logger.PersistenceOperationFailed(persistenceException);
                    }

                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Elasticsearch dependency is unavailable." });
                    return;
                }

                Activity.Current.SetTelemetryError(
                    TelemetryErrorCategory.Handler,
                    exception,
                    recordException: false);
                using (BeginRequestScope(app.Logger, context))
                {
                    app.Logger.UnexpectedServerError(exception);
                }

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

        app.MapImagingPipelinePrometheusScrapingEndpoint();
        await app.RunAsync();
    }

    private static IDisposable? BeginRequestScope(ILogger logger, HttpContext context) =>
        logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new(TelemetryAttributeNames.HttpRequestMethod, context.Request.Method),
            new(TelemetryAttributeNames.HttpRoute, ResolveRequestRoute(context))
        });

    private static string ResolveRequestRoute(HttpContext context)
    {
        var endpoint = context.Features.Get<IExceptionHandlerFeature>()?.Endpoint
            ?? context.GetEndpoint();
        if (endpoint is not RouteEndpoint routeEndpoint)
        {
            return "<unmatched>";
        }

        var route = routeEndpoint.RoutePattern.RawText;
        if (string.IsNullOrWhiteSpace(route))
        {
            return "<unmatched>";
        }

        return route.StartsWith('/') ? route : $"/{route}";
    }
}
