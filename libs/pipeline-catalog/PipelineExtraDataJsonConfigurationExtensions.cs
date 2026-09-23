using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;

namespace ImagingPipeline.PipelineCatalog;

public static class PipelineExtraDataJsonConfigurationExtensions
{
    /// <summary>
    /// Preserves pipeline ExtraData objects as complete JSON values in existing JSON file sources.
    /// Sources retain their positions and file settings, so later providers override the entire value.
    /// Call before adding stream sources to a live ConfigurationManager.
    /// </summary>
    public static IConfigurationBuilder PreservePipelineExtraDataJson(this IConfigurationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var indexes = builder.Sources
            .Select((source, index) => (source, index))
            .Where(item => item.source is JsonConfigurationSource)
            .Select(item => item.index)
            .ToArray();

        if (indexes.Length == 0)
        {
            return builder;
        }

        // Replacing any source on ConfigurationManager rebuilds every provider. Consumed
        // streams cannot be replayed safely; wrap file sources before streams are added.
        if (builder is ConfigurationManager && builder.Sources.Any(source => source is StreamConfigurationSource))
        {
            throw new InvalidOperationException(
                "Call PreservePipelineExtraDataJson before adding stream configuration sources. " +
                "Use AddPipelineCatalogJsonStream to preserve ExtraData in a new stream.");
        }

        foreach (var index in indexes)
        {
            builder.Sources[index] = new ExtraDataJsonFileSource((JsonConfigurationSource)builder.Sources[index]);
        }

        return builder;
    }

    /// <summary>
    /// Adds JSON from a stream, retaining pipeline ExtraData JSON types and empty containers.
    /// Like AddJsonStream, the source is read once and does not support reload.
    /// </summary>
    public static IConfigurationBuilder AddPipelineCatalogJsonStream(this IConfigurationBuilder builder, Stream stream)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(stream);
        return builder.Add(new ExtraDataJsonStreamSource { Stream = stream });
    }

    private sealed class ExtraDataJsonFileSource(JsonConfigurationSource original) : IConfigurationSource
    {
        public IConfigurationProvider Build(IConfigurationBuilder builder)
        {
            original.EnsureDefaults(builder);
            return new ExtraDataJsonFileProvider(original);
        }
    }

    private sealed class ExtraDataJsonFileProvider(JsonConfigurationSource source) : JsonConfigurationProvider(source)
    {
        public override void Load(Stream stream)
        {
            using var preserved = PipelineExtraDataJsonDocument.Preserve(stream);
            base.Load(preserved);
        }
    }

    private sealed class ExtraDataJsonStreamSource : JsonStreamConfigurationSource
    {
        public override IConfigurationProvider Build(IConfigurationBuilder builder) => new ExtraDataJsonStreamProvider(this);
    }

    private sealed class ExtraDataJsonStreamProvider(JsonStreamConfigurationSource source) : JsonStreamConfigurationProvider(source)
    {
        public override void Load(Stream stream)
        {
            using var preserved = PipelineExtraDataJsonDocument.Preserve(stream);
            base.Load(preserved);
        }
    }
}
