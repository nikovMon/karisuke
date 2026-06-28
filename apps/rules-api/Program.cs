using System.Text.Json.Serialization;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Repositories;
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
        builder.Services.AddControllers()
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
                var fields = context.ModelState
                    .Where(item => item.Value?.Errors.Count > 0)
                    .Select(item => item.Key)
                    .Take(20)
                    .ToArray();
                var errorCount = context.ModelState.Values.Sum(value => value.Errors.Count);

                logger.LogWarning(
                    "Request model validation failed for {RequestMethod} {RequestPath}. ErrorCount: {ErrorCount}; Fields: {ValidationFields}; TraceId: {TraceId}",
                    context.HttpContext.Request.Method,
                    context.HttpContext.Request.Path.Value,
                    errorCount,
                    string.Join(", ", fields),
                    context.HttpContext.TraceIdentifier);

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
            .Validate(options => options.IsValid(out _), "Rules Elasticsearch configuration is invalid.")
            .ValidateOnStart();
        builder.Services.AddScoped<IRuleRepository, ElasticsearchRuleRepository>();
        builder.Services.AddScoped<IRuleService, RuleService>();
        builder.Services.AddSingleton<IElasticsearchHealthProbe, ElasticsearchHealthProbe>();

        var app = builder.Build();

        app.UseMiddleware<RequestLoggingMiddleware>();
        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                if (exception is RuleRepositoryException repositoryException)
                {
                    app.Logger.LogError(
                        repositoryException,
                        "Elasticsearch repository operation failed for {RequestMethod} {RequestPath}. TraceId: {TraceId}",
                        context.Request.Method,
                        context.Request.Path.Value,
                        context.TraceIdentifier);
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Elasticsearch dependency is unavailable." });
                    return;
                }

                app.Logger.LogError(
                    exception,
                    "Unexpected server error for {RequestMethod} {RequestPath}. TraceId: {TraceId}",
                    context.Request.Method,
                    context.Request.Path.Value,
                    context.TraceIdentifier);
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
}
