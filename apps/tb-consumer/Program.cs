using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;

namespace ImagingPipeline.TbConsumer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.TbConsumer);
        builder.ConfigureImagingPipelinePrometheusListener();

        // RabbitMQ consumer services (also registers IRabbitMqPublisher)
        builder.Services.AddRabbitMqConsumer(builder.Configuration);

        // Projection Mapper Client
        builder.Services.AddProjectionMapperClient(builder.Configuration);

        // Core Pipeline
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<EmbedderInputMessageBuilder>();
        builder.Services.AddSingleton<TbMessageHandler>();

        builder.Services.AddHostedService<Worker>();

        var app = builder.Build();
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        await app.RunAsync();
    }
}
