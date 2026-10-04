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

        var configuredFile = builder.Configuration["PipelineCatalogFile"];
        var catalogFile = configuredFile is null
            ? Path.Combine(AppContext.BaseDirectory, PipelineCatalogFileConfigurationExtensions.DefaultFileName)
            : Path.GetFullPath(configuredFile, builder.Environment.ContentRootPath);
        var catalog = new ConfigurationBuilder().AddPipelineCatalogJsonFile(catalogFile);

        // Load one complete catalog, with native ExtraData JSON preserved. Standard host
        // configuration remains higher priority, including whole-object environment/CLI overrides.
        for (var index = 0; index < catalog.Sources.Count; index++)
            builder.Configuration.Sources.Insert(index, catalog.Sources[index]);

        return builder;
    }
}
