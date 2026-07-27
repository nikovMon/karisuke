using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class PipelineTelemetry
{
    private static readonly Counter<long> Messages = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.PipelineMessages, "{message}", "Pipeline messages by stage, direction, and outcome.");
    private static readonly Histogram<long> PayloadSize = TelemetryMeters.Pipeline.CreateHistogram<long>(
        TelemetryMetricNames.PipelinePayloadSize, "By", "Pipeline message payload size by stage and direction.");
    private static readonly Histogram<double> StageDuration = TelemetryMeters.Pipeline.CreateHistogram<double>(
        TelemetryMetricNames.PipelineStageDuration, "s", "Application work duration for a pipeline stage.");
    private static readonly Histogram<double> ExternalStageDuration = TelemetryMeters.Pipeline.CreateHistogram<double>(
        TelemetryMetricNames.PipelineExternalStageDuration,
        "s",
        "Elapsed transit through an external pipeline stage, including its surrounding queues.");
    private static readonly Histogram<long> FanOut = TelemetryMeters.Pipeline.CreateHistogram<long>(
        TelemetryMetricNames.PipelineFanOut, "{message}", "Output messages generated per input message.");
    private static readonly Histogram<long> BatchSize = TelemetryMeters.Pipeline.CreateHistogram<long>(
        TelemetryMetricNames.PipelineBatchSize, "{item}", "Items processed together by a pipeline stage.");
    private static readonly Histogram<double> EndToEndDuration = TelemetryMeters.Pipeline.CreateHistogram<double>(
        TelemetryMetricNames.PipelineEndToEndDuration, "s", "Pipeline origin-to-current-stage duration.");
    private static readonly Counter<long> InvalidTimingHeaders = TelemetryMeters.Pipeline.CreateCounter<long>(
        TelemetryMetricNames.InvalidTimingHeaders,
        "{header}",
        "Timing headers rejected before they can corrupt latency measurements.");

    public static void RecordMessage(
        PipelineStage stage,
        PipelineDirection direction,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None,
        long count = 1)
    {
        Messages.Add(Math.Max(0, count), Tags(stage, direction, outcome, error));
    }

    public static void RecordStageDuration(
        PipelineStage stage,
        double durationSeconds,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
    {
        var tags = StageTags(stage);
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        StageDuration.Record(Math.Max(0, durationSeconds), tags);
    }

    public static void RecordExternalStageDuration(PipelineStage stage, double durationSeconds) =>
        ExternalStageDuration.Record(Math.Max(0, durationSeconds), StageTags(stage));

    public static void RecordPayloadSize(PipelineStage stage, PipelineDirection direction, long bytes)
    {
        var tags = StageTags(stage);
        tags.Add(TelemetryAttributeNames.PipelineDirection, direction.Value());
        PayloadSize.Record(Math.Max(0, bytes), tags);
    }

    public static void RecordFanOut(PipelineStage stage, long outputCount)
    {
        var tags = StageTags(stage);
        FanOut.Record(Math.Max(0, outputCount), tags);
    }

    public static void RecordBatchSize(PipelineStage stage, PipelineItem item, long count)
    {
        var tags = StageTags(stage);
        tags.Add(TelemetryAttributeNames.PipelineItem, item.Value());
        BatchSize.Record(Math.Max(0, count), tags);
    }

    public static void RecordEndToEndDuration(PipelineStage stage, double durationSeconds)
    {
        var tags = StageTags(stage);
        EndToEndDuration.Record(Math.Max(0, durationSeconds), tags);
    }

    public static void RecordInvalidTimingHeader(
        TimingHeaderKind header,
        TimingHeaderRejectionReason reason)
    {
        var tags = new TagList
        {
            { "imaging_pipeline.telemetry.header", header.Value() },
            { "imaging_pipeline.telemetry.rejection.reason", reason.Value() }
        };
        InvalidTimingHeaders.Add(1, tags);
    }

    private static TagList Tags(
        PipelineStage stage,
        PipelineDirection direction,
        TelemetryOutcome outcome,
        TelemetryErrorCategory error)
    {
        var tags = StageTags(stage);
        tags.Add(TelemetryAttributeNames.PipelineDirection, direction.Value());
        tags.Add(TelemetryAttributeNames.PipelineOutcome, outcome.Value());
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        return tags;
    }

    private static TagList StageTags(PipelineStage stage) => new()
    {
        { TelemetryAttributeNames.PipelineStage, stage.Value() }
    };
}
