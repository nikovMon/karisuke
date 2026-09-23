using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.PipelineCatalog;

public static class PipelineCatalogServiceCollectionExtensions
{
    public static IServiceCollection AddPipelineCatalog(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<PipelineCatalogOptions>()
            // .NET's binder includes a failed scalar value in conversion exceptions. Validate
            // arbitrary body data first so malformed values never reach that error path.
            .Configure(_ => ValidateExtraDataConfiguration(configuration))
            .Bind(
                configuration.GetSection(PipelineCatalogOptions.SectionName),
                binder => binder.ErrorOnUnknownConfiguration = true)
            .ValidateOnStart();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IValidateOptions<PipelineCatalogOptions>, PipelineCatalogOptionsValidator>());
        services.TryAddSingleton<PipelineCatalog>();
        services.TryAddSingleton<IPipelineCatalog>(provider => provider.GetRequiredService<PipelineCatalog>());
        services.TryAddSingleton<IRuleSourceResolver>(provider => provider.GetRequiredService<PipelineCatalog>());
        services.TryAddSingleton<IRabbitMqConnectionResolver>(provider => provider.GetRequiredService<PipelineCatalog>());
        return services;
    }

    private static void ValidateExtraDataConfiguration(IConfiguration configuration)
    {
        // Use configuration child paths: collection binding compacts sparse numeric indexes,
        // so a bound list position is not necessarily the source section's index.
        var pipelines = configuration.GetSection(PipelineCatalogOptions.SectionName)
            .GetSection(nameof(PipelineCatalogOptions.Pipelines));
        foreach (var pipeline in pipelines.GetChildren())
        {
            var extraData = pipeline.GetChildren().FirstOrDefault(section =>
                section.Key.Equals(nameof(PipelineDefinition.ExtraData), StringComparison.OrdinalIgnoreCase));
            if (extraData is null) continue;

            if (extraData.GetChildren().Any())
            {
                throw new InvalidOperationException(
                    $"Configuration '{extraData.Path}' must contain one complete JSON object; child-key overrides are not supported.");
            }
            if (extraData.Value is not { } json)
            {
                throw new InvalidOperationException(
                    $"Configuration '{extraData.Path}' must be a JSON object; omit it to use an empty object.");
            }

            try
            {
                _ = PipelineExtraData.Parse(json);
            }
            catch (FormatException)
            {
                // Do not retain a binder/parser exception or the configured body data.
                throw new InvalidOperationException(
                    $"Configuration '{extraData.Path}' must contain a valid JSON object.");
            }
        }
    }
}
