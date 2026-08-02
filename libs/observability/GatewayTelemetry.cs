using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class GatewayTelemetry
{
    private static long _ruleCacheEntries;
    private static long _ruleCacheSkippedRules;
    private static long _lastSuccessfulRefreshUnixMilliseconds;

    private static readonly Counter<long> CacheRefreshes = TelemetryMeters.Gateway.CreateCounter<long>(
        TelemetryMetricNames.GatewayRuleCacheRefreshes, "{refresh}", "Rule-cache refresh attempts.");
    private static readonly Histogram<double> CacheRefreshDuration = TelemetryMeters.Gateway.CreateHistogram<double>(
        TelemetryMetricNames.GatewayRuleCacheRefreshDuration, "s", "Rule-cache refresh duration.");
    private static readonly Histogram<long> RulesEvaluated = TelemetryMeters.Gateway.CreateHistogram<long>(
        TelemetryMetricNames.GatewayRulesEvaluated, "{rule}", "Rules evaluated for one input message.");
    private static readonly Histogram<long> RulesMatched = TelemetryMeters.Gateway.CreateHistogram<long>(
        TelemetryMetricNames.GatewayRulesMatched, "{rule}", "Rules matched for one input message.");
    private static readonly Counter<long> RulesFilteredPhotoAge = TelemetryMeters.Gateway.CreateCounter<long>(
        TelemetryMetricNames.GatewayRulesFilteredPhotoAge,
        "{rule}",
        "Rules excluded because an image exceeded the configured maximum photo age.");

    static GatewayTelemetry()
    {
        TelemetryMeters.Gateway.CreateObservableGauge(
            TelemetryMetricNames.GatewayRuleCacheEntries,
            static () => Volatile.Read(ref _ruleCacheEntries),
            "{rule}",
            "Rules in this pod's active snapshot.");
        TelemetryMeters.Gateway.CreateObservableGauge(
            TelemetryMetricNames.GatewayRuleCacheSkippedRules,
            static () => Volatile.Read(ref _ruleCacheSkippedRules),
            "{rule}",
            "Invalid rules skipped while building this pod's active snapshot.");
        TelemetryMeters.Gateway.CreateObservableGauge(
            TelemetryMetricNames.GatewayRuleCacheAge,
            ObserveCacheAgeSeconds,
            "s",
            "Seconds since this pod's last successful rule-cache refresh.");
    }

    public static void RecordCacheRefresh(
        double durationSeconds,
        TelemetryOutcome outcome,
        int entries,
        TelemetryErrorCategory error = TelemetryErrorCategory.None,
        int skippedRules = 0)
    {
        var tags = new TagList { { TelemetryAttributeNames.PipelineOutcome, outcome.Value() } };
        if (error != TelemetryErrorCategory.None)
        {
            tags.Add("error.type", error.Value());
        }

        CacheRefreshes.Add(1, tags);
        CacheRefreshDuration.Record(Math.Max(0, durationSeconds), tags);
        if (outcome == TelemetryOutcome.Success)
        {
            Volatile.Write(ref _ruleCacheEntries, Math.Max(0, entries));
            Volatile.Write(ref _ruleCacheSkippedRules, Math.Max(0, skippedRules));
            Volatile.Write(ref _lastSuccessfulRefreshUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public static void RecordRuleMatching(long evaluated, long matched)
    {
        RulesEvaluated.Record(Math.Max(0, evaluated));
        RulesMatched.Record(Math.Max(0, matched));
    }

    public static void RecordPhotoAgeFilteredRules(long count)
    {
        if (count > 0)
        {
            RulesFilteredPhotoAge.Add(count);
        }
    }

    private static double ObserveCacheAgeSeconds()
    {
        var refreshedAt = Volatile.Read(ref _lastSuccessfulRefreshUnixMilliseconds);
        return refreshedAt == 0
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - refreshedAt) / 1_000d);
    }
}
