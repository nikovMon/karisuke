using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Configuration;
using ImagingPipeline.UnifiedGateway.Processing;

namespace ImagingPipeline.UnifiedGateway;

public sealed partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = GatewayApplicationBuilder.Create(args);
        builder.AddImagingPipelineObservability(
            ObservabilityServiceNames.UnifiedGateway,
            instrumentAspNetCore: true);
        builder.ConfigureImagingPipelinePrometheusListener();

        builder.Services.AddSingleton<IPipelineContract, AsdPipelineContract>();
        builder.Services.AddSingleton<IPipelineContractRegistry, PipelineContractRegistry>();
        builder.Services.AddPipelineCatalog(builder.Configuration);
        builder.Services.AddSingleton<PipelineWorkPreparer>();

        var app = builder.Build();
        var catalog = app.Services.GetRequiredService<IPipelineCatalog>();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "Healthy",
            capabilities = new[] { "catalog", "contracts" },
            dispatchActive = false
        }));
        app.MapGet("/pipelines", (IPipelineCatalog pipelines) => Results.Ok(
            pipelines.GetAll().Select(pipeline => new
            {
                pipeline.PipelineId,
                pipeline.Enabled,
                pipeline.ContractId
            })));
        app.MapGet("/pipelines/{pipelineId}", (string pipelineId, IPipelineCatalog pipelines) =>
        {
            var pipeline = pipelines.GetAll().FirstOrDefault(candidate => candidate.PipelineId == pipelineId);
            return pipeline is null ? Results.NotFound() : Results.Ok(new
            {
                pipeline.PipelineId,
                pipeline.Enabled,
                pipeline.ContractId
            });
        });
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        app.Logger.LogInformation(
            "Unified gateway catalog initialized with {PipelineCount} pipelines and {EnabledPipelineCount} enabled. " +
            "Catalog and contract preparation are available; event consumption and transport dispatch are not active.",
            catalog.GetAll().Count,
            catalog.GetEnabled().Count);
        await app.RunAsync();
    }
}
