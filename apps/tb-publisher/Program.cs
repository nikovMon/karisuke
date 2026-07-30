using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.MessageHandling;
using ImagingPipeline.TbPublisher.Processing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImagingPipeline.TbPublisher;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.TbPublisher);
        builder.ConfigureImagingPipelinePrometheusListener();

        builder.Services.AddRabbitMqConsumer(builder.Configuration);
        builder.Services.AddProjectionMapperClient(builder.Configuration);

        builder.Services.AddSingleton<IInputMessageValidator, InputMessageValidator>();
        builder.Services.AddSingleton<TbPublisherGeometryConverter>();
        builder.Services.AddSingleton<ITbPublisherOutputMessageBuilder, TbPublisherOutputMessageBuilder>();
        builder.Services.AddSingleton<IRabbitMqMessageHandler, TbPublisherMessageHandler>();

        builder.Services.AddHostedService<Worker>();

        var app = builder.Build();
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        await app.RunAsync();
    }
}
