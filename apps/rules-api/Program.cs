using System.Text.Json.Serialization;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
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

        var app = builder.Build();

        app.UseExceptionHandler(exceptionApp =>
        {
            exceptionApp.Run(async context =>
            {
                var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
                if (exception is RuleRepositoryException repositoryException)
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = repositoryException.Message });
                    return;
                }

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                await context.Response.WriteAsJsonAsync(new { error = "Unexpected server error." });
            });
        });

        app.UseSwagger();
        app.UseSwaggerUI();
        app.MapControllers();
        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

        await app.RunAsync();
    }
}
