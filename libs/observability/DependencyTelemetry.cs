using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class DependencyTelemetry
{
    private static readonly Counter<long> Operations = TelemetryMeters.Dependencies.CreateCounter<long>(
        TelemetryMetricNames.DependencyOperations, "{operation}", "Logical dependency operations.");
    private static readonly Histogram<double> Duration = TelemetryMeters.Dependencies.CreateHistogram<double>(
        TelemetryMetricNames.DependencyDuration, "s", "Logical dependency operation duration.");
    private static readonly Histogram<long> PayloadSize = TelemetryMeters.Dependencies.CreateHistogram<long>(
        TelemetryMetricNames.DependencyPayloadSize, "By", "Dependency request or response payload size.");
    private static readonly Histogram<long> BatchSize = TelemetryMeters.Dependencies.CreateHistogram<long>(
        TelemetryMetricNames.DependencyBatchSize, "{item}", "Number of items in a dependency operation.");

    public static void RecordOperation(
        DependencyName dependency,
        DependencyOperation operation,
        double durationSeconds,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = Tags(dependency, operation, outcome, error);
        Operations.Add(1, tags);
        Duration.Record(Math.Max(0, durationSeconds), tags);
    }

    public static void RecordPayloadSize(
        DependencyName dependency,
        DependencyOperation operation,
        PipelineDirection direction,
        long bytes)
    {
        var tags = BaseTags(dependency, operation);
        tags.Add(TelemetryAttributeNames.PipelineDirection, direction.Value());
        PayloadSize.Record(Math.Max(0, bytes), tags);
    }

    public static void RecordBatchSize(
        DependencyName dependency,
        DependencyOperation operation,
        PipelineItem item,
        long count)
    {
        var tags = BaseTags(dependency, operation);
        tags.Add(TelemetryAttributeNames.PipelineItem, item.Value());
        BatchSize.Record(Math.Max(0, count), tags);
    }

    private static TagList Tags(
        DependencyName dependency,
        DependencyOperation operation,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error)
    {
        var tags = BaseTags(dependency, operation);
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        return tags;
    }

    private static TagList BaseTags(
        DependencyName dependency,
        DependencyOperation operation) => new()
    {
        { TelemetryAttributeNames.DependencyName, dependency.Value() },
        { TelemetryAttributeNames.DependencyOperation, operation.Value() }
    };
}
