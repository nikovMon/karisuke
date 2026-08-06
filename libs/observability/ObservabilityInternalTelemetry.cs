using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

internal static class ObservabilityInternalTelemetry
{
    private static long _queuedLogs;

    private static readonly Counter<long> DroppedLogs = TelemetryMeters.Observability.CreateCounter<long>(
        TelemetryMetricNames.LogsDropped,
        "{log_record}",
        "Log records dropped before Logstash accepted them.");

    private static readonly Counter<long> ExportRequests = TelemetryMeters.Observability.CreateCounter<long>(
        TelemetryMetricNames.LogExportRequests,
        "{request}",
        "Logstash HTTP export requests.");

    private static readonly Histogram<double> ExportDuration = TelemetryMeters.Observability.CreateHistogram<double>(
        TelemetryMetricNames.LogExportDuration,
        "s",
        "Duration of Logstash HTTP export requests.");

    static ObservabilityInternalTelemetry()
    {
        TelemetryMeters.Observability.CreateObservableGauge(
            TelemetryMetricNames.LogQueueSize,
            static () => Volatile.Read(ref _queuedLogs),
            "{log_record}",
            "Log records waiting in the in-process Logstash buffer.");
    }

    public static void AddQueuedLogs(int delta) => Interlocked.Add(ref _queuedLogs, delta);

    public static void RecordDroppedLogs(string reason, long count = 1)
    {
        var tags = new TagList { { "findair.logs.drop.reason", reason } };
        DroppedLogs.Add(Math.Max(0, count), tags);
    }

    public static void RecordExport(
        double durationSeconds,
        string outcome,
        string? statusCodeClass = null)
    {
        var tags = new TagList
        {
            { TelemetryAttributeNames.PipelineOutcome, outcome }
        };
        if (!string.IsNullOrWhiteSpace(statusCodeClass))
        {
            tags.Add("http.response.status_code_class", statusCodeClass);
        }

        ExportRequests.Add(1, tags);
        ExportDuration.Record(Math.Max(0, durationSeconds), tags);
    }
}
