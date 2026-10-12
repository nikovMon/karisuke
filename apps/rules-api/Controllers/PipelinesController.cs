using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using Microsoft.AspNetCore.Mvc;

namespace ImagingPipeline.Rules.Api.Controllers;

/// <summary>
/// Read-only view of the pipeline catalog. Lists every configured pipeline, enabled or not,
/// because rules can be written for a pipeline before it is switched on.
/// </summary>
[ApiController]
[Route("pipelines")]
[Produces("application/json")]
public sealed class PipelinesController : ControllerBase
{
    private readonly IPipelineCatalog _catalog;
    private readonly ILogger<PipelinesController> _logger;

    public PipelinesController(IPipelineCatalog catalog, ILogger<PipelinesController> logger)
    {
        _catalog = catalog;
        _logger = logger;
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
    public IActionResult GetById(string pipelineId)
    {
        var pipeline = _catalog.GetAll().FirstOrDefault(pipeline => pipeline.PipelineId == pipelineId);
        if (pipeline is null)
        {
            using (_logger.BeginScope(new KeyValuePair<string, object?>[]
                   {
                       new(TelemetryAttributeNames.PipelineId, pipelineId)
                   }))
            {
                _logger.PipelineNotFound();
            }

            return NotFound();
        }

        return Ok(ToResponse(pipeline));
    }

    private static PipelineResponse ToResponse(PipelineDefinition pipeline) =>
        new(pipeline.PipelineId, pipeline.ContractId, pipeline.Enabled);
}
