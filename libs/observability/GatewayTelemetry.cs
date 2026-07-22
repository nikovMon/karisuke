using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.Observability;

public static class GatewayTelemetry
{
    private static long ruleCacheEntries;
    private static long lastSuccessfulRefreshUnixMilliseconds;

    private static readonly Counter<long> CacheRefreshes = TelemetryMeters.Gateway.CreateCounter<long>(
        TelemetryMetricNames.GatewayRuleCacheRefreshes, "{refresh}", "Rule-cache refresh attempts.");
    private static readonly Histogram<double> CacheRefreshDuration = TelemetryMeters.Gateway.CreateHistogram<double>(
        TelemetryMetricNames.GatewayRuleCacheRefreshDuration, "s", "Rule-cache refresh duration.");
    private static readonly Histogram<long> RulesEvaluated = TelemetryMeters.Gateway.CreateHistogram<long>(
        TelemetryMetricNames.GatewayRulesEvaluated, "{rule}", "Rules evaluated for one input message.");
    private static readonly Histogram<long> RulesMatched = TelemetryMeters.Gateway.CreateHistogram<long>(
        TelemetryMetricNames.GatewayRulesMatched, "{rule}", "Rules matched for one input message.");

    static GatewayTelemetry()
    {
        TelemetryMeters.Gateway.CreateObservableGauge(
            TelemetryMetricNames.GatewayRuleCacheEntries,
            () => Volatile.Read(ref ruleCacheEntries),
            "{rule}",
            "Rules in this pod's active snapshot.");
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
        TelemetryErrorCategory error = TelemetryErrorCategory.None)
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
            Volatile.Write(ref ruleCacheEntries, Math.Max(0, entries));
            Volatile.Write(ref lastSuccessfulRefreshUnixMilliseconds, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        }
    }

    public static void RecordRuleMatching(long evaluated, long matched)
    {
        RulesEvaluated.Record(Math.Max(0, evaluated));
        RulesMatched.Record(Math.Max(0, matched));
    }

    private static double ObserveCacheAgeSeconds()
    {
        var refreshedAt = Volatile.Read(ref lastSuccessfulRefreshUnixMilliseconds);
        return refreshedAt == 0
            ? 0
            : Math.Max(0, (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - refreshedAt) / 1_000d);
    }
}
