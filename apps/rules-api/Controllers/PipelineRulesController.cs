using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Observability;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Controllers;

/// <summary>
/// v2 rules of one pipeline, stored in that pipeline's own index.
/// </summary>
[ApiController]
[Route("pipelines/{pipelineId}/rules")]
[TypeFilter(typeof(RulesOperationFilter), Order = 0)]
[TypeFilter(typeof(KnownPipelineFilter), Order = 1)]
[Produces("application/json")]
public sealed class PipelineRulesController : ControllerBase
{
    private readonly PipelineRuleService _service;
    private readonly RulesElasticsearchOptions _options;

    public PipelineRulesController(
        PipelineRuleService service,
        IOptions<RulesElasticsearchOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<PipelineRuleDocument>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAll(
        [FromRoute] string pipelineId,
        [FromQuery] bool getNameOnly = false,
        [FromQuery] bool? isActive = null,
        [FromQuery] int from = 0,
        [FromQuery] int? size = null,
        CancellationToken cancellationToken = default)
    {
        var searchSize = size ?? _options.DefaultSearchSize;
        if (_options.ValidatePage(from, searchSize) is { } error)
        {
            return BadRequest(new { error });
        }

        return getNameOnly
            ? Ok(await _service.GetRuleNamesAsync(pipelineId, isActive, from, searchSize, cancellationToken))
            : Ok(await _service.GetRulesAsync(pipelineId, isActive, from, searchSize, cancellationToken));
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(PipelineRuleDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] string pipelineId,
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var rule = await _service.GetByIdAsync(pipelineId, id, cancellationToken);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpGet("name/{ruleName}")]
    [ProducesResponseType(typeof(PipelineRuleDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByName(
        [FromRoute] string pipelineId,
        [FromRoute] string ruleName,
        CancellationToken cancellationToken = default)
    {
        var rule = await _service.GetByNameAsync(pipelineId, ruleName, cancellationToken);
        return rule is null ? NotFound() : Ok(rule);
    }
}
