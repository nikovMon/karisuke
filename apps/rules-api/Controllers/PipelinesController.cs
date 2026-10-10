using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.Rules.Api.Observability;
using Microsoft.AspNetCore.Mvc;

namespace ImagingPipeline.Rules.Api.Controllers;

/// <summary>
/// Read-only view of the pipeline catalog. Lists every configured pipeline, enabled or not,
/// because rules can be written for a pipeline before it is switched on.
/// </summary>
[ApiController]
[Route("pipelines")]
[TypeFilter(typeof(KnownPipelineFilter))]
[Produces("application/json")]
public sealed class PipelinesController : ControllerBase
{
    private readonly IPipelineCatalog _catalog;

    public PipelinesController(IPipelineCatalog catalog)
    {
        _catalog = catalog;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineResponse>), StatusCodes.Status200OK)]
    public IActionResult GetAll() => Ok(_catalog.GetAll()
        .OrderBy(pipeline => pipeline.PipelineId, StringComparer.Ordinal)
        .Select(ToResponse)
        .ToList());

    [HttpGet("{pipelineId}")]
    [ProducesResponseType(typeof(PipelineResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetById(string pipelineId) =>
        Ok(ToResponse(_catalog.GetRequired(pipelineId)));

    private static PipelineResponse ToResponse(PipelineDefinition pipeline) =>
        new(pipeline.PipelineId, pipeline.ContractId, pipeline.Enabled);
}
