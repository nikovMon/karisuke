using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.UnifiedGateway.Configuration;
using ImagingPipeline.UnifiedGateway.Dispatch;
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
        builder.Services.AddSingleton<IPipelineContract, AlgoPipelineContract>();
        builder.Services.AddSingleton<IPipelineContractRegistry, PipelineContractRegistry>();
        builder.Services.AddPipelineCatalog(builder.Configuration);
        builder.Services.AddSingleton<PipelineWorkPreparer>();
        builder.Services.AddRabbitMqDestinationPublisher(builder.Configuration);
        builder.Services.AddSingleton<IDispatchTransport, RabbitMqDispatchTransport>();
        // Redirects are not followed and the catalog timeout is applied per request by the transport.
        builder.Services.AddHttpClient(HttpDispatchTransport.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IDispatchTransport, HttpDispatchTransport>();
        builder.Services.AddSingleton<PipelineDispatcher>();

        var app = builder.Build();
        var catalog = app.Services.GetRequiredService<IPipelineCatalog>();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "Healthy",
            capabilities = new[] { "catalog", "contracts" },
            dispatchActive = false
        }));
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        app.Logger.LogInformation(
            "Unified gateway catalog initialized with {PipelineCount} pipelines and {EnabledPipelineCount} enabled. " +
            "Catalog and contract preparation are available; event consumption and transport dispatch are not active.",
            catalog.GetAll().Count,
            catalog.GetEnabled().Count);
        await app.RunAsync();
    }
}
