using System.Diagnostics;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Infrastructure;

namespace ImagingPipeline.Rules.Api.Observability;

internal sealed class RulesOperationFilter : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(
        ActionExecutingContext context,
        ActionExecutionDelegate next)
    {
        var operation = ResolveActionOperation(context.ActionDescriptor.RouteValues["action"]);
        var requestedCount = RequestedDocumentCount(context);
        var startedAt = TelemetryTiming.StartTimestamp();
        using var activity = TelemetrySources.RulesApi.StartActivity(
            SpanName(operation),
            ActivityKind.Internal);

        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag("imaging_pipeline.rules.operation", OperationName(operation));
            if (requestedCount.HasValue)
            {
                activity.SetTag(
                    "imaging_pipeline.rules.request.document.count",
                    requestedCount.Value);
            }
        }

        if (requestedCount.HasValue)
        {
            RulesTelemetry.RecordBatchSize(operation, requestedCount.Value);
        }

        if (context.ActionArguments.TryGetValue("id", out var id))
        {
            activity.AddPipelineContext(ruleId: id as string);
        }

        try
        {
            var executed = await next();
            if (executed.Exception is { } exception && !executed.ExceptionHandled)
            {
                RecordException(activity, operation, startedAt, exception, context.HttpContext.RequestAborted);
                return;
            }

            var statusCode = GetStatusCode(executed.Result);
            var documentCount = GetDocumentCount(executed.Result, statusCode);
            var outcome = statusCode switch
            {
                >= 200 and < 300 => TelemetryOutcome.Success,
                >= 400 and < 500 => TelemetryOutcome.Rejected,
                _ => TelemetryOutcome.Failure
            };
            var error = statusCode switch
            {
                StatusCodes.Status400BadRequest => TelemetryErrorCategory.Validation,
                StatusCodes.Status503ServiceUnavailable => TelemetryErrorCategory.Dependency,
                >= 500 => TelemetryErrorCategory.Handler,
                _ => TelemetryErrorCategory.None
            };

            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("http.response.status_code", statusCode);
                activity.SetTag("imaging_pipeline.rules.response.document.count", documentCount);
                activity.SetTag(TelemetryAttributeNames.PipelineOutcome, OutcomeName(outcome));
            }
            if (outcome == TelemetryOutcome.Success)
            {
                activity.SetTelemetrySuccess();
            }
            else if (outcome == TelemetryOutcome.Failure)
            {
                activity.SetTelemetryError(error);
            }

            RulesTelemetry.RecordOperation(
                operation,
                TelemetryTiming.ElapsedSeconds(startedAt),
                outcome,
                documentCount,
                error);
            if (statusCode == StatusCodes.Status400BadRequest)
            {
                RulesTelemetry.RecordValidationFailure(operation);
            }
        }
        catch (Exception exception)
        {
            RecordException(activity, operation, startedAt, exception, context.HttpContext.RequestAborted);
            throw;
        }
    }

    internal static RulesOperation ResolveRequestOperation(HttpRequest request)
    {
        var path = request.Path.Value ?? string.Empty;
        if (HttpMethods.IsPost(request.Method))
        {
            return RulesOperation.Create;
        }

        if (HttpMethods.IsDelete(request.Method))
        {
            return RulesOperation.Delete;
        }

        if (HttpMethods.IsPatch(request.Method))
        {
            if (path.EndsWith("/activity", StringComparison.OrdinalIgnoreCase))
            {
                return RulesOperation.SetActivity;
            }

            if (path.EndsWith("/bulk", StringComparison.OrdinalIgnoreCase))
            {
                return RulesOperation.BulkUpdate;
            }

            if (path.Contains("/sensors/add", StringComparison.OrdinalIgnoreCase))
            {
                return RulesOperation.AddSensor;
            }

            if (path.Contains("/sensors/remove", StringComparison.OrdinalIgnoreCase))
            {
                return RulesOperation.RemoveSensor;
            }

            return RulesOperation.Update;
        }

        if (path.Contains("/name/", StringComparison.OrdinalIgnoreCase))
        {
            return RulesOperation.GetByName;
        }

        return path.TrimEnd('/').Count(character => character == '/') > 1
            ? RulesOperation.GetById
            : RulesOperation.Search;
    }

    private static void RecordException(
        Activity? activity,
        RulesOperation operation,
        long startedAt,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
        var outcome = cancelled ? TelemetryOutcome.Cancelled : TelemetryOutcome.Failure;
        var error = exception switch
        {
            OperationCanceledException when cancelled => TelemetryErrorCategory.Cancelled,
            RulePersistenceException => TelemetryErrorCategory.Dependency,
            _ => TelemetryErrorCategory.Handler
        };
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineOutcome, OutcomeName(outcome));
        }
        // The ASP.NET boundary and structured error log own exception details.
        activity.SetTelemetryError(error, exception, recordException: false);
        RulesTelemetry.RecordOperation(
            operation,
            TelemetryTiming.ElapsedSeconds(startedAt),
            outcome,
            error: error);
    }

    private static RulesOperation ResolveActionOperation(string? actionName) => actionName switch
    {
        "GetAll" => RulesOperation.Search,
        "GetById" => RulesOperation.GetById,
        "GetByName" => RulesOperation.GetByName,
        "Create" => RulesOperation.Create,
        "Update" => RulesOperation.Update,
        "UpdateBulk" => RulesOperation.BulkUpdate,
        "ChangeActivity" => RulesOperation.SetActivity,
        "AddSensors" => RulesOperation.AddSensor,
        "RemoveSensors" => RulesOperation.RemoveSensor,
        "Delete" => RulesOperation.Delete,
        _ => RulesOperation.Search
    };

    private static long? RequestedDocumentCount(ActionExecutingContext context)
    {
        if (!context.ActionArguments.TryGetValue("ids", out var ids) || ids is not string rawIds)
        {
            return null;
        }

        long count = 0;
        var containsValue = false;
        foreach (var character in rawIds)
        {
            if (character == ',')
            {
                if (containsValue)
                {
                    count++;
                    containsValue = false;
                }

                continue;
            }

            containsValue |= !char.IsWhiteSpace(character);
        }

        return containsValue ? count + 1 : count;
    }

    private static int GetStatusCode(IActionResult? result) => result switch
    {
        IStatusCodeActionResult { StatusCode: { } statusCode } => statusCode,
        _ => StatusCodes.Status200OK
    };

    private static long GetDocumentCount(IActionResult? result, int statusCode) => result switch
    {
        ObjectResult { Value: BulkOperationResult bulk } => bulk.SuccessIds.Count,
        ObjectResult { Value: IReadOnlyCollection<RuleDto> rules } => rules.Count,
        ObjectResult { Value: IReadOnlyCollection<string> names } => names.Count,
        ObjectResult { Value: RuleDto } => 1,
        _ when statusCode == StatusCodes.Status204NoContent => 1,
        _ => 0
    };

    private static string OperationName(RulesOperation operation) => operation switch
    {
        RulesOperation.Search => "search",
        RulesOperation.GetById => "get_by_id",
        RulesOperation.GetByName => "get_by_name",
        RulesOperation.Create => "create",
        RulesOperation.Update => "update",
        RulesOperation.BulkUpdate => "bulk_update",
        RulesOperation.SetActivity => "set_activity",
        RulesOperation.AddSensor => "add_sensor",
        RulesOperation.RemoveSensor => "remove_sensor",
        RulesOperation.Delete => "delete",
        _ => "operation"
    };

    private static string SpanName(RulesOperation operation) => operation switch
    {
        RulesOperation.Search => "rules.search",
        RulesOperation.GetById => "rules.get_by_id",
        RulesOperation.GetByName => "rules.get_by_name",
        RulesOperation.Create => "rules.create",
        RulesOperation.Update => "rules.update",
        RulesOperation.BulkUpdate => "rules.bulk_update",
        RulesOperation.SetActivity => "rules.set_activity",
        RulesOperation.AddSensor => "rules.add_sensor",
        RulesOperation.RemoveSensor => "rules.remove_sensor",
        RulesOperation.Delete => "rules.delete",
        _ => "rules.operation"
    };

    private static string OutcomeName(TelemetryOutcome outcome) => outcome switch
    {
        TelemetryOutcome.Success => "success",
        TelemetryOutcome.Rejected => "rejected",
        TelemetryOutcome.Cancelled => "cancelled",
        _ => "failure"
    };
}
