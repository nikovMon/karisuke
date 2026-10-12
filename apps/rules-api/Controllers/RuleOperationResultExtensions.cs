using ImagingPipeline.Common.Dtos.Rules.Responses;
using Microsoft.AspNetCore.Mvc;

namespace ImagingPipeline.Rules.Api.Controllers;

/// <summary>Maps rule operation results to HTTP responses, the same way for every rules route.</summary>
internal static class RuleOperationResultExtensions
{
    public static IActionResult ToActionResult<T>(
        this ControllerBase controller,
        RuleOperationResult<T> result,
        Func<T, IActionResult>? created = null) =>
        result.Status switch
        {
            RuleOperationStatus.Success when created is not null => created(result.Value!),
            RuleOperationStatus.Success => controller.Ok(result.Value),
            RuleOperationStatus.PartialSuccess => controller.StatusCode(StatusCodes.Status207MultiStatus, result.Value),
            RuleOperationStatus.AllFailed => controller.UnprocessableEntity(result.Value),
            _ => controller.ToErrorResult(result.Status, result.Error)
        };

    public static IActionResult ToActionResult(this ControllerBase controller, RuleOperationResult result) =>
        result.Status == RuleOperationStatus.Success
            ? controller.NoContent()
            : controller.ToErrorResult(result.Status, result.Error);

    private static IActionResult ToErrorResult(this ControllerBase controller, RuleOperationStatus status, string? error) =>
        status switch
        {
            RuleOperationStatus.ValidationFailed => controller.BadRequest(new { error }),
            RuleOperationStatus.NotFound => controller.NotFound(new { error }),
            RuleOperationStatus.Conflict => controller.Conflict(new { error }),
            _ => controller.StatusCode(StatusCodes.Status500InternalServerError)
        };
}
