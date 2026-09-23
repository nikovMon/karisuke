namespace ImagingPipeline.PipelineCatalog;

public interface IPipelineCatalog
{
    IReadOnlyList<PipelineDefinition> GetAll();
    IReadOnlyList<PipelineDefinition> GetEnabled();
    PipelineDefinition GetRequired(string pipelineId);
}

public interface IRuleSourceResolver
{
    RuleSource Resolve(string pipelineId);
}

public interface IRabbitMqConnectionResolver
{
    RabbitMqConnectionOptions GetRequired(string connectionRef);
}

// PipelineId is required even when IndexName is shared. Consumers must use both values.
public sealed record RuleSource(string IndexName, string PipelineId);
