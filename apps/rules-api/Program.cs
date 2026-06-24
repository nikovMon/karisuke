namespace ImagingPipeline.Rules.Api;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new { status = "Healthy" }));

        await app.RunAsync();
    }
}
