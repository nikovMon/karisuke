using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;

namespace ImagingPipeline.UnifiedGateway.Processing;

public enum PipelinePreparationStatus
{
    Prepared,
    Disabled,
    Invalid
}

public sealed record PreparedPipelineWork(PipelineDefinition Pipeline, PipelinePayload Payload);

public sealed record PipelinePreparationResult(
    PipelinePreparationStatus Status,
    PreparedPipelineWork? Work,
    IReadOnlyList<ContractValidationError> Errors);
