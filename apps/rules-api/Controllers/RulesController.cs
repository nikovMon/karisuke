using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Configuration;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Controllers;

[ApiController]
[Route("rules")]
[Produces("application/json")]
public sealed class RulesController : ControllerBase
{
    private readonly IRuleService _service;
    private readonly RulesElasticsearchOptions _options;

    public RulesController(
        IRuleService service,
        IOptions<RulesElasticsearchOptions> options)
    {
        _service = service;
        _options = options.Value;
    }

    // Read routes
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<RuleConfigDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAll(
        [FromQuery] bool getNameOnly = false,
        [FromQuery] bool? isActive = null,
        [FromQuery] int from = 0,
        [FromQuery] int? size = null,
        CancellationToken cancellationToken = default)
    {
        var searchSize = size ?? _options.DefaultSearchSize;
        if (from < 0 || searchSize <= 0 || searchSize > _options.MaxSearchSize)
        {
            return BadRequest(new
            {
                error = $"from must be at least 0 and size must be between 1 and {_options.MaxSearchSize}."
            });
        }

        if (getNameOnly)
        {
            var names = await _service.GetRuleNamesAsync(isActive, from, searchSize, cancellationToken);
            return Ok(names);
        }

        var rules = await _service.GetRulesAsync(getNameOnly, isActive, from, searchSize, cancellationToken);
        return Ok(rules);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(RuleConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var rule = await _service.GetByIdAsync(id, cancellationToken);
        return rule is null ? NotFound() : Ok(rule);
    }

    [HttpGet("name/{ruleName}")]
    [ProducesResponseType(typeof(RuleConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByName(
        [FromRoute] string ruleName,
        CancellationToken cancellationToken = default)
    {
        var rule = await _service.GetByNameAsync(ruleName, cancellationToken);
        return rule is null ? NotFound() : Ok(rule);
    }

    // Create route
    [HttpPost]
    [ProducesResponseType(typeof(RuleConfigDto), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(
        [FromBody] RuleConfigDto rule,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.CreateAsync(rule, cancellationToken);
        return ToActionResult(result, createdAtId: result.Value?.Id);
    }

    // Update routes
    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(RuleConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromRoute] string id,
        [FromBody] UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.UpdateAsync(id, request, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPatch("bulk")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateBulk(
        [FromQuery] string ids,
        [FromBody] UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.UpdateBulkAsync(ParseIds(ids), request, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPatch("{id}/activity")]
    [ProducesResponseType(typeof(RuleConfigDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ChangeActivity(
        [FromRoute] string id,
        [FromBody] ChangeRuleActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.ChangeActivityAsync(id, request, cancellationToken);
        return ToActionResult(result);
    }

    // Sensor routes
    [HttpPatch("sensors/add")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> AddSensors(
        [FromQuery] string ids,
        [FromBody] RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.AddSensorsAsync(ParseIds(ids), request, cancellationToken);
        return ToActionResult(result);
    }

    [HttpPatch("sensors/remove")]
    [ProducesResponseType(typeof(BulkOperationResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RemoveSensors(
        [FromQuery] string ids,
        [FromBody] RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.RemoveSensorsAsync(ParseIds(ids), request, cancellationToken);
        return ToActionResult(result);
    }

    // Delete route
    [HttpDelete("{id}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(
        [FromRoute] string id,
        CancellationToken cancellationToken = default)
    {
        var result = await _service.DeleteAsync(id, cancellationToken);
        return result.Status == RuleOperationStatus.Success ? NoContent() : ToActionResult(result);
    }

    private IActionResult ToActionResult<T>(RuleOperationResult<T> result, string? createdAtId = null)
    {
        return result.Status switch
        {
            RuleOperationStatus.Success when createdAtId is not null =>
                CreatedAtAction(nameof(GetById), new { id = createdAtId }, result.Value),
            RuleOperationStatus.Success => Ok(result.Value),
            RuleOperationStatus.ValidationFailed => BadRequest(new { error = result.Error }),
            RuleOperationStatus.NotFound => NotFound(new { error = result.Error }),
            RuleOperationStatus.Conflict => Conflict(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private IActionResult ToActionResult(RuleOperationResult result)
    {
        return result.Status switch
        {
            RuleOperationStatus.Success => Ok(),
            RuleOperationStatus.ValidationFailed => BadRequest(new { error = result.Error }),
            RuleOperationStatus.NotFound => NotFound(new { error = result.Error }),
            RuleOperationStatus.Conflict => Conflict(new { error = result.Error }),
            _ => StatusCode(StatusCodes.Status500InternalServerError)
        };
    }

    private static IReadOnlyList<string> ParseIds(string? ids) =>
        string.IsNullOrWhiteSpace(ids)
            ? []
            : ids.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
}
