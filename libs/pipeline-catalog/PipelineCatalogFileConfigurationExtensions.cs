using Microsoft.Extensions.Configuration;

namespace ImagingPipeline.PipelineCatalog;

public static class PipelineCatalogFileConfigurationExtensions
{
    public const string DefaultFileName = "pipelinecatalog.json";

    /// <summary>
    /// Loads a reusable catalog file while preserving contract-specific ExtraData JSON values.
    /// Add host overrides, environment variables and command-line sources after this source.
    /// </summary>
    public static IConfigurationBuilder AddPipelineCatalogJsonFile(
        this IConfigurationBuilder builder, string path, bool optional = false, bool reloadOnChange = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        // Stage before adding to a live ConfigurationManager, which loads a source immediately.
        var sourceBuilder = new ConfigurationBuilder().AddJsonFile(path, optional, reloadOnChange);
        sourceBuilder.PreservePipelineExtraDataJson();
        foreach (var source in sourceBuilder.Sources)
            builder.Add(source);
        return builder;
    }
}
