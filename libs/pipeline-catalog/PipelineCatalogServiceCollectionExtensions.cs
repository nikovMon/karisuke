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
            .Configure(_ => ValidatePipelineConfiguration(configuration))
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

    private static void ValidatePipelineConfiguration(IConfiguration configuration)
    {
        var pipelineIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pipelineId in EnumeratePipelineIds(configuration))
        {
            if (pipelineIds.TryGetValue(pipelineId, out var existing) &&
                !string.Equals(existing, pipelineId, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Configuration '{PipelineCatalogOptions.SectionName}:Pipelines' contains pipeline IDs differing only in case. Use the same ID spelling in every configuration source.");
            }
            pipelineIds[pipelineId] = pipelineId;
        }

        // Inspect each pipeline's raw configuration before binding can discard malformed values.
        var pipelines = configuration.GetSection(PipelineCatalogOptions.SectionName)
            .GetSection(nameof(PipelineCatalogOptions.Pipelines));
        if (pipelines.Value is not null)
            throw new InvalidOperationException($"Configuration '{pipelines.Path}' must be an object keyed by pipeline ID.");
        foreach (var pipeline in pipelines.GetChildren())
        {
            if (pipeline.Value is not null)
                throw new InvalidOperationException($"Configuration '{pipeline.Path}' must be an object.");
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

    private static IEnumerable<string> EnumeratePipelineIds(IConfiguration configuration)
    {
        const string path = $"{PipelineCatalogOptions.SectionName}:{nameof(PipelineCatalogOptions.Pipelines)}";
        if (configuration is IConfigurationRoot root)
        {
            foreach (var provider in root.Providers)
            {
                var ids = provider is ChainedConfigurationProvider chained
                    ? EnumeratePipelineIds(chained.Configuration)
                    : provider.GetChildKeys([], path);
                foreach (var id in ids)
                    yield return id;
            }
        }
        else
        {
            foreach (var pipeline in configuration.GetSection(path).GetChildren())
                yield return pipeline.Key;
        }
    }
}
