namespace ImagingPipeline.Gateway;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);

        builder.Services.AddHostedService<Worker>();

        await builder.Build().RunAsync();
    }
}
