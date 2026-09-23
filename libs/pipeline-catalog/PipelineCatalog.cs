using System.Collections.Frozen;
using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.PipelineCatalog;

public sealed class PipelineCatalog : IPipelineCatalog, IRuleSourceResolver, IRabbitMqConnectionResolver
{
    private readonly IReadOnlyList<PipelineDefinition> _all;
    private readonly IReadOnlyList<PipelineDefinition> _enabled;
    private readonly FrozenDictionary<string, PipelineDefinition> _byId;
    private readonly FrozenDictionary<string, RabbitMqConnectionOptions> _connections;

    public PipelineCatalog(
        IOptions<PipelineCatalogOptions> options,
        IPipelineContractRegistry contracts,
        ILogger<PipelineCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentNullException.ThrowIfNull(logger);
        var configured = options.Value;
        var validation = new PipelineCatalogOptionsValidator(contracts).Validate(Options.DefaultName, configured);
        if (validation.Failed)
        {
            throw new OptionsValidationException(
                Options.DefaultName, typeof(PipelineCatalogOptions), validation.Failures);
        }

        _all = Array.AsReadOnly(configured.Pipelines.Select(Clone).ToArray());
        _enabled = Array.AsReadOnly(_all.Where(pipeline => pipeline.Enabled).ToArray());
        _byId = _all.ToFrozenDictionary(pipeline => pipeline.PipelineId, StringComparer.Ordinal);
        _connections = configured.RabbitMqConnections.ToFrozenDictionary(
            connection => connection.Key, connection => connection.Value with { }, StringComparer.Ordinal);
        logger.LogInformation(
            "Pipeline catalog initialized with {PipelineCount} pipelines, {EnabledPipelineCount} enabled.",
            _all.Count, _enabled.Count);
    }

    public IReadOnlyList<PipelineDefinition> GetAll() => Array.AsReadOnly(_all.Select(Clone).ToArray());
    public IReadOnlyList<PipelineDefinition> GetEnabled() => Array.AsReadOnly(_enabled.Select(Clone).ToArray());

    public PipelineDefinition GetRequired(string pipelineId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipelineId);
        return _byId.TryGetValue(pipelineId, out var pipeline)
            ? Clone(pipeline)
            : throw new KeyNotFoundException($"Pipeline '{pipelineId}' is not configured.");
    }

    public RuleSource Resolve(string pipelineId)
    {
        var pipeline = GetRequired(pipelineId);
        return new RuleSource(pipeline.RulesIndex, pipeline.PipelineId);
    }

    RabbitMqConnectionOptions IRabbitMqConnectionResolver.GetRequired(string connectionRef)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionRef);
        return _connections.TryGetValue(connectionRef, out var connection)
            ? connection
            : throw new KeyNotFoundException("The RabbitMQ connection reference is not configured.");
    }

    private static PipelineDefinition Clone(PipelineDefinition pipeline) => pipeline with
    {
        Transport = pipeline.Transport with
        {
            RabbitMq = pipeline.Transport.RabbitMq is { } rabbit
                ? rabbit with { Output = RabbitMqOptionsValidation.NormalizeQueue(rabbit.Output) }
                : null,
            Http = pipeline.Transport.Http is { } http
                ? http with { Headers = new Dictionary<string, string>(http.Headers, StringComparer.OrdinalIgnoreCase) }
                : null
        }
    };
}
