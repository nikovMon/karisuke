using ImagingPipeline.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Configuration;

public static class GatewayApplicationBuilder
{
    public static WebApplicationBuilder Create(string[] args, WebApplicationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(args);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = options?.Args ?? args,
            ApplicationName = options?.ApplicationName,
            EnvironmentName = options?.EnvironmentName,
            ContentRootPath = options?.ContentRootPath,
            WebRootPath = options?.WebRootPath
        });

        builder.Configuration.AddPipelineCatalogFileAsBase(builder.Environment.ContentRootPath);
        return builder;
    }
}
