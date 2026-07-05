using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbConsumer.Application;

namespace ImagingPipeline.TbConsumer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddRabbitMqConsumer(builder.Configuration);
        builder.Services.AddSingleton<TbMessageHandler>();
        builder.Services.AddHostedService<Worker>();

        await builder.Build().RunAsync();
    }
}
