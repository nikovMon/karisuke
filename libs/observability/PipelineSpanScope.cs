using System.Diagnostics;

namespace ImagingPipeline.Observability;

/// <summary>
/// A pipeline stage span that carries its own bookkeeping: stage and operation tags on creation,
/// pipeline context attached up front, and an <see cref="ActivityStatusCode.Ok"/> status on
/// disposal unless the caller already reported a failure.
/// </summary>
/// <remarks>
/// Every tag write is guarded by <see cref="Activity.IsAllDataRequested"/>, so callers can set tags
/// unconditionally without repeating the guard at each site.
/// </remarks>
public readonly struct PipelineSpanScope : IDisposable
{
    private PipelineSpanScope(Activity? activity) => Activity = activity;

    /// <summary>The underlying activity, or <c>null</c> when nothing is listening.</summary>
    public Activity? Activity { get; }

    /// <summary>
    /// Starts an internal span for a step within a stage, named <c>{stage}.{operation}</c>
    /// — for example <c>tb_consumer.projection</c>.
    /// </summary>
    public static PipelineSpanScope StartStage(
        PipelineStage stage,
        string operation,
        in TelemetryLogContext context)
    {
        var activity = TelemetrySources.For(stage).StartActivity(
            $"{stage.Value()}.{operation}",
            ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, stage.Value());
            activity.SetTag("findair.operation", operation);
        }

        activity.AddPipelineContext(context);
        return new PipelineSpanScope(activity);
    }

    /// <summary>
    /// Starts a producer span for work that publishes messages onward, under a caller-chosen name
    /// because the span crosses into the downstream stage's vocabulary.
    /// </summary>
    public static PipelineSpanScope StartProducer(
        PipelineStage stage,
        string name,
        in TelemetryLogContext context)
    {
        var activity = TelemetrySources.For(stage).StartActivity(name, ActivityKind.Producer);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, stage.Value());
        }

        activity.AddPipelineContext(context);
        return new PipelineSpanScope(activity);
    }

    public void SetTag(string key, object? value)
    {
        if (Activity?.IsAllDataRequested == true)
        {
            Activity.SetTag(key, value);
        }
    }

    /// <summary>
    /// Marks the span failed. <paramref name="recordException"/> stays explicit because the pipeline
    /// only attaches exception events where the handler boundary does not already log the exception.
    /// </summary>
    public void Failed(
        TelemetryErrorCategory category,
        Exception? exception = null,
        bool recordException = true) =>
        Activity.SetTelemetryError(category, exception, recordException);

    public void Cancelled(Exception? exception = null) =>
        Activity.SetTelemetryError(TelemetryErrorCategory.Cancelled, exception, recordException: false);

    public void Dispose()
    {
        if (Activity is null)
        {
            return;
        }

        // Unset means no failure was reported, so the span completed successfully.
        if (Activity.Status == ActivityStatusCode.Unset)
        {
            Activity.SetTelemetrySuccess();
        }

        Activity.Dispose();
    }
}
