using ImagingPipeline.ProjectionMapperClient;

namespace ImagingPipeline.TbConsumer;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddProjectionMapperClient(builder.Configuration);
        builder.Services.AddHostedService<Worker>();

        await builder.Build().RunAsync();
    }
}
