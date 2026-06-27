using System.Text.Json.Serialization;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Health;
using ImagingPipeline.Rules.Api.Repositories;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Diagnostics;

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

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                if (exception is RuleRepositoryException repositoryException)
                {
                    app.Logger.LogError(repositoryException, "Elasticsearch repository operation failed.");
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Elasticsearch dependency is unavailable." });
                    return;
                }

                app.Logger.LogError(exception, "Unexpected server error.");
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
