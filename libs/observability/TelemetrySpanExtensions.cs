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
        string? algorithmName = null)
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
        return activity;
    }

    private static void SetIfPresent(Activity activity, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            activity.SetTag(key, value);
        }
    }
}
