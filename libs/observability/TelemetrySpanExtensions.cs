using System.Diagnostics;

namespace ImagingPipeline.Observability;

public static class TelemetrySpanExtensions
{
    public static Activity? SetTelemetryError(
        this Activity? activity,
        TelemetryErrorCategory category,
        Exception? exception = null,
        bool recordException = true)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return null;
        }

        activity.SetStatus(ActivityStatusCode.Error);
        activity.SetTag(TelemetryAttributeNames.ErrorCategory, category.Value());
        // Use the same bounded classification on the operation span and its metrics.
        // The concrete CLR type remains available on the exception event.
        activity.SetTag("error.type", category.Value());

        if (exception is not null && recordException && activity.IsAllDataRequested)
        {
            var tags = new ActivityTagsCollection
            {
                { "exception.type", exception.GetType().FullName },
                { "exception.message", exception.Message },
                { "exception.stacktrace", exception.ToString() }
            };
            activity.AddEvent(new ActivityEvent("exception", tags: tags));
        }

        return activity;
    }

    public static Activity? SetTelemetrySuccess(this Activity? activity)
    {
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetStatus(ActivityStatusCode.Ok);
        }

        return activity;
    }

    public static Activity? AddPipelineContext(
        this Activity? activity,
        string? taskId = null,
        string? requestId = null,
        string? imageId = null,
        string? ruleId = null,
        string? tenantId = null,
        string? algorithmName = null,
        string? areaName = null,
        string? sensorName = null,
        string? tileId = null,
        int? tileIndex = null)
    {
        if (activity is null || !activity.IsAllDataRequested)
        {
            return activity;
        }

        SetIfPresent(activity, TelemetryAttributeNames.PipelineTaskId, taskId);
        SetIfPresent(activity, TelemetryAttributeNames.PipelineRequestId, requestId);
        SetIfPresent(activity, TelemetryAttributeNames.PipelineImageId, imageId);
        SetIfPresent(activity, TelemetryAttributeNames.PipelineRuleId, ruleId);
        SetIfPresent(activity, TelemetryAttributeNames.PipelineTenantId, tenantId);
        SetIfPresent(activity, TelemetryAttributeNames.PipelineAlgorithmName, algorithmName);
        SetIfPresent(activity, TelemetryAttributeNames.AreaName, areaName);
        SetIfPresent(activity, TelemetryAttributeNames.SensorName, sensorName);
        SetIfPresent(activity, TelemetryAttributeNames.TileId, tileId);
        if (tileIndex.HasValue)
        {
            activity.SetTag(TelemetryAttributeNames.TileIndex, tileIndex.Value);
        }

        return activity;
    }

    /// <summary>
    /// Attaches the pipeline fields of a log context to a span, so a handler that already built
    /// its logging context does not repeat the same values argument by argument.
    /// </summary>
    public static Activity? AddPipelineContext(this Activity? activity, in TelemetryLogContext context) =>
        activity.AddPipelineContext(
            taskId: context.TaskId,
            requestId: context.RequestId,
            imageId: context.ImageId,
            ruleId: context.RuleId,
            tenantId: context.TenantId,
            algorithmName: context.AlgorithmName,
            areaName: context.AreaName,
            sensorName: context.SensorName,
            tileId: context.TileId,
            tileIndex: context.TileIndex);

    private static void SetIfPresent(Activity activity, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(key, value);
        }
    }
}
