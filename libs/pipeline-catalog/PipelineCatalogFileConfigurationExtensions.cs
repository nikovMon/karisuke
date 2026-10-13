using Microsoft.Extensions.Configuration;

namespace ImagingPipeline.PipelineCatalog;

public static class PipelineCatalogFileConfigurationExtensions
{
    public const string DefaultFileName = "pipelinecatalog.json";

    /// <summary>The setting that points a host at a catalog file other than the one shipped with it.</summary>
    public const string FileSettingName = "PipelineCatalogFile";

    /// <summary>
    /// Loads the host's catalog file as its lowest-priority configuration, so appsettings files,
    /// environment variables and the command line can still override any catalog value.
    /// The file is the one named by <see cref="FileSettingName"/>, relative to the content root,
    /// or the shipped <see cref="DefaultFileName"/> next to the application.
    /// </summary>
    public static IConfigurationManager AddPipelineCatalogFileAsBase(
        this IConfigurationManager configuration, string contentRootPath)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRootPath);
        var configuredFile = configuration[FileSettingName];
        var catalogFile = configuredFile is null
            ? Path.Combine(AppContext.BaseDirectory, DefaultFileName)
            : Path.GetFullPath(configuredFile, contentRootPath);
        var catalog = new ConfigurationBuilder().AddPipelineCatalogJsonFile(catalogFile);

        for (var index = 0; index < catalog.Sources.Count; index++)
            configuration.Sources.Insert(index, catalog.Sources[index]);
        return configuration;
    }

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
