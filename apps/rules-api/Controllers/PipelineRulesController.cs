using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
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

    [HttpPost]
    [ProducesResponseType(typeof(PipelineRuleDocument), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromRoute] string pipelineId,
        [FromBody] CreatePipelineRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CreateAsync(pipelineId, request, cancellationToken);
        return this.ToActionResult(result, rule => CreatedAtAction(nameof(GetById), new { pipelineId, id = rule.Id }, rule));
    }

    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(PipelineRuleDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromRoute] string pipelineId,
        [FromRoute] string id,
        [FromBody] UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.UpdateAsync(pipelineId, id, request, cancellationToken));

    [HttpPatch("bulk")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status207MultiStatus)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> UpdateBulk(
        [FromRoute] string pipelineId,
        [FromQuery] string ids,
        [FromBody] UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.UpdateBulkAsync(pipelineId, RuleIdsQuery.Parse(ids), request, cancellationToken));

    [HttpPatch("sensors/add")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status207MultiStatus)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> AddSensors(
        [FromRoute] string pipelineId,
        [FromQuery] string ids,
        [FromBody] RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.AddSensorsAsync(pipelineId, RuleIdsQuery.Parse(ids), request, cancellationToken));

    [HttpPatch("sensors/remove")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status207MultiStatus)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> RemoveSensors(
        [FromRoute] string pipelineId,
        [FromQuery] string ids,
        [FromBody] RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.RemoveSensorsAsync(pipelineId, RuleIdsQuery.Parse(ids), request, cancellationToken));

    [HttpPatch("{id}/activity")]
    [ProducesResponseType(typeof(PipelineRuleDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeActivity(
        [FromRoute] string pipelineId,
        [FromRoute] string id,
        [FromBody] ChangeRuleActivationStatusRequest request,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.ChangeActivityAsync(pipelineId, id, request.IsActive!.Value, cancellationToken));

    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute] string pipelineId,
        [FromRoute] string id,
        CancellationToken cancellationToken = default) =>
        this.ToActionResult(await _service.DeleteAsync(pipelineId, id, cancellationToken));
}
