using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class RulesTelemetry
{
    private static readonly Counter<long> Operations = TelemetryMeters.RulesApi.CreateCounter<long>(
        TelemetryMetricNames.RulesOperations, "{operation}", "Rules API logical operations.");
    private static readonly Histogram<double> Duration = TelemetryMeters.RulesApi.CreateHistogram<double>(
        TelemetryMetricNames.RulesOperationDuration, "s", "Rules API logical operation duration.");
    private static readonly Histogram<long> Documents = TelemetryMeters.RulesApi.CreateHistogram<long>(
        TelemetryMetricNames.RulesDocuments, "{document}", "Documents returned or changed by a Rules operation.");
    private static readonly Histogram<long> BatchSize = TelemetryMeters.RulesApi.CreateHistogram<long>(
        TelemetryMetricNames.RulesBatchSize, "{document}", "Documents requested in a Rules bulk operation.");
    private static readonly Counter<long> ValidationFailures = TelemetryMeters.RulesApi.CreateCounter<long>(
        TelemetryMetricNames.RulesValidationFailures, "{failure}", "Rules request validation failures.");

    public static void RecordOperation(
        RulesOperation operation,
        double durationSeconds,
        TelemetryOutcome outcome,
        long documentCount = 0,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = Tags(operation, outcome, error);
        Operations.Add(1, tags);
        Duration.Record(Math.Max(0, durationSeconds), tags);
        Documents.Record(Math.Max(0, documentCount), tags);
    }

    public static void RecordBatchSize(RulesOperation operation, long count)
    {
        var tags = new TagList
        {
            { "imaging_pipeline.rules.operation", operation.Value() }
        };
        BatchSize.Record(Math.Max(0, count), tags);
    }

    public static void RecordValidationFailure(RulesOperation operation)
    {
        var tags = Tags(operation, TelemetryOutcome.Rejected, TelemetryErrorCategory.Validation);
        ValidationFailures.Add(1, tags);
    }

    private static TagList Tags(
        RulesOperation operation,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error)
    {
        var tags = new TagList
        {
            { "imaging_pipeline.rules.operation", operation.Value() },
            { TelemetryAttributeNames.PipelineOutcome, outcome.Value() }
        };
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        return tags;
    }
}
