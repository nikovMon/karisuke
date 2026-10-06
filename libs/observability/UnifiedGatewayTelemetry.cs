using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class UnifiedGatewayTelemetry
{
    private static readonly Counter<long> DispatchUnits = TelemetryMeters.UnifiedGateway.CreateCounter<long>(
        TelemetryMetricNames.UnifiedGatewayDispatchUnits, "{unit}", "Dispatch outcomes by pipeline and transport.");
    private static readonly Histogram<double> DispatchDuration = TelemetryMeters.UnifiedGateway.CreateHistogram<double>(
        TelemetryMetricNames.UnifiedGatewayDispatchDuration, "s", "Time spent sending one unit over its transport.");

    /// <summary>
    /// Records one dispatched unit. Pipeline ID and transport kind come from the deployment catalog,
    /// so they are bounded.
    /// </summary>
    public static void RecordDispatch(
        string pipelineId,
        string transportKind,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error,
        double durationSeconds)
    {
        var tags = new TagList
        {
            { "pipeline.id", pipelineId },
            { "pipeline.transport", transportKind },
            { TelemetryAttributeNames.PipelineOutcome, outcome.Value() }
        };
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        DispatchUnits.Add(1, tags);
        DispatchDuration.Record(Math.Max(0, durationSeconds), tags);
    }
}
