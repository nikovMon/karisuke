using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Diagnostics;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class ActiveRuleCache : IHostedService, IDisposable
{
    private readonly IRuleRepository _repository;
    private readonly GatewayGeometryConverter _geometry;
    private readonly GatewayHealthState _healthState;
    private readonly ILogger<ActiveRuleCache> _logger;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _refreshJitter;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;
    private IReadOnlyList<ActiveRule> _current = [];

    public ActiveRuleCache(
        IRuleRepository repository,
        GatewayGeometryConverter geometry,
        IOptions<GatewaySettings> settings,
        GatewayHealthState healthState,
        ILogger<ActiveRuleCache> logger)
    {
        _repository = repository;
        _geometry = geometry;
        _healthState = healthState;
        _logger = logger;
        _refreshInterval = TimeSpan.FromSeconds(settings.Value.RuleRefreshIntervalSeconds);
        _refreshJitter = TimeSpan.FromSeconds(settings.Value.RuleRefreshJitterSeconds);
    }

    public IReadOnlyList<ActiveRule> Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = TelemetryTiming.StartTimestamp();
        using var activity = StartRefreshActivity("initial");
        try
        {
            var initialRules = BuildSnapshot(
                await _repository.GetActiveRulesAsync(cancellationToken),
                out var skippedCount);
            Volatile.Write(ref _current, initialRules);
            _healthState.MarkRulesRefreshSucceeded();
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Success,
                initialRules.Length);
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("imaging_pipeline.gateway.rule_cache.entries", initialRules.Length);
                activity.SetTag("imaging_pipeline.gateway.rule_cache.skipped", skippedCount);
            }
            activity.SetTelemetrySuccess();
            _logger.RuleCacheInitialized(initialRules.Length, skippedCount);
        }
        catch (OperationCanceledException ex)
        {
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Cancelled,
                Current.Count,
                TelemetryErrorCategory.Cancelled);
            activity.SetTelemetryError(
                TelemetryErrorCategory.Cancelled,
                ex,
                recordException: false);
            throw;
        }
        catch (Exception ex)
        {
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Failure,
                Current.Count,
                TelemetryErrorCategory.Dependency);
            activity.SetTelemetryError(
                TelemetryErrorCategory.Dependency,
                ex,
                recordException: false);
            _logger.RuleCacheRefreshFailed(ex, Current.Count);
            throw;
        }

        _refreshCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _refreshTask = Task.Run(() => RefreshLoopAsync(_refreshCancellation.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_refreshCancellation is null || _refreshTask is null)
        {
            return;
        }

        await _refreshCancellation.CancelAsync();

        try
        {
            await _refreshTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task RefreshLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(NextRefreshDelay(), cancellationToken);
            await RefreshAsync(cancellationToken);
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var started = TelemetryTiming.StartTimestamp();
        using var activity = StartRefreshActivity("scheduled");
        try
        {
            var rules = BuildSnapshot(
                await _repository.GetActiveRulesAsync(cancellationToken),
                out var skippedCount);
            Volatile.Write(ref _current, rules);
            _healthState.MarkRulesRefreshSucceeded();
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Success,
                rules.Length);
            if (activity?.IsAllDataRequested == true)
            {
                activity.SetTag("imaging_pipeline.gateway.rule_cache.entries", rules.Length);
                activity.SetTag("imaging_pipeline.gateway.rule_cache.skipped", skippedCount);
            }
            activity.SetTelemetrySuccess();
            _logger.RuleCacheRefreshed(rules.Length, skippedCount);
        }
        catch (OperationCanceledException ex) when (cancellationToken.IsCancellationRequested)
        {
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Cancelled,
                Current.Count,
                TelemetryErrorCategory.Cancelled);
            activity.SetTelemetryError(
                TelemetryErrorCategory.Cancelled,
                ex,
                recordException: false);
            throw;
        }
        catch (Exception ex)
        {
            // Keep the last valid snapshot when a refresh fails.
            _healthState.MarkRulesRefreshFailed();
            GatewayTelemetry.RecordCacheRefresh(
                TelemetryTiming.ElapsedSeconds(started),
                TelemetryOutcome.Failure,
                Current.Count,
                TelemetryErrorCategory.Dependency);
            activity.SetTelemetryError(
                TelemetryErrorCategory.Dependency,
                ex,
                recordException: false);
            _logger.RuleCacheRefreshFailed(ex, Current.Count);
        }
    }

    private TimeSpan NextRefreshDelay()
    {
        if (_refreshJitter <= TimeSpan.Zero)
        {
            return _refreshInterval;
        }

        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * _refreshJitter.TotalMilliseconds);
        return _refreshInterval + jitter;
    }

    private ActiveRule[] BuildSnapshot(IReadOnlyList<RuleDto> rules, out int skippedCount)
    {
        const int sampleLimit = 10;
        var snapshot = new List<ActiveRule>(rules.Count);
        var captureWarningDetails = _logger.IsEnabled(LogLevel.Warning);
        List<string>? skippedRuleSample = null;
        Exception? firstFailure = null;
        skippedCount = 0;
        foreach (var rule in rules)
        {
            try
            {
                snapshot.Add(BuildSnapshot(rule));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                skippedCount++;
                if (captureWarningDetails)
                {
                    firstFailure ??= ex;
                    if (skippedRuleSample is null || skippedRuleSample.Count < sampleLimit)
                    {
                        skippedRuleSample ??= new List<string>(sampleLimit);
                        skippedRuleSample.Add(
                            $"{NormalizeRuleLogValue(rule.Id)}:{NormalizeRuleLogValue(rule.RuleName)}");
                    }
                }
            }
        }

        if (skippedCount > 0 && firstFailure is not null)
        {
            _logger.RuleCacheRulesSkipped(
                firstFailure,
                skippedCount,
                string.Join(", ", skippedRuleSample!),
                Math.Max(0, skippedCount - sampleLimit));
        }

        return snapshot.ToArray();
    }

    private static string NormalizeRuleLogValue(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "unknown" : value;

    private static Activity? StartRefreshActivity(string refreshType)
    {
        var activity = TelemetrySources.Gateway.StartActivity("gateway.rule_cache.refresh", ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "gateway");
            activity.SetTag("imaging_pipeline.gateway.rule_cache.refresh.type", refreshType);
        }
        return activity;
    }

    private ActiveRule BuildSnapshot(RuleDto rule) =>
        new(
            rule.Id,
            rule.AlgorithmName,
            BuildSensorSnapshot(rule.Sensors),
            BuildTenantSnapshot(rule.TenantsInfo),
            rule.MinimumResolution,
            rule.MaximumResolution,
            _geometry.ReadRuleGeometry(rule));

    private static IReadOnlyDictionary<string, IReadOnlySet<string>> BuildSensorSnapshot(
        IReadOnlyDictionary<string, List<string>>? sensors)
    {
        if (sensors is null || sensors.Count == 0)
        {
            return new Dictionary<string, IReadOnlySet<string>>(0, StringComparer.Ordinal);
        }

        var snapshot = new Dictionary<string, IReadOnlySet<string>>(sensors.Count, StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
            snapshot[sensor.Key] = sensor.Value.ToHashSet(StringComparer.Ordinal);
        }

        return snapshot;
    }

    private static TenantInfo[] BuildTenantSnapshot(IReadOnlyList<TenantInfo> tenants)
    {
        var snapshot = new TenantInfo[tenants.Count];
        for (var tenantIndex = 0; tenantIndex < tenants.Count; tenantIndex++)
        {
            var tenant = tenants[tenantIndex];
            snapshot[tenantIndex] = new TenantInfo
            {
                TenantId = tenant.TenantId,
                TilingConfigs = tenant.TilingConfigs
                    .Select(tiling => new TilingConfig
                    {
                        TileSizeWidth = tiling.TileSizeWidth,
                        TileSizeHeight = tiling.TileSizeHeight,
                        TileOverlapWidth = tiling.TileOverlapWidth,
                        TileOverlapHeight = tiling.TileOverlapHeight
                    })
                    .ToList()
            };
        }

        return snapshot;
    }

    public void Dispose()
    {
        _refreshCancellation?.Dispose();
    }
}
