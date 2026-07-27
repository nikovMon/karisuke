using System.ComponentModel.DataAnnotations;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Errors;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Observability;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using System.Diagnostics;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class ActiveRuleCache : IHostedService, IDisposable
{
    private const int MaxDetailedInvalidRuleWarningsPerLoad = 10;
    private const int MaxValidationErrorsPerRuleWarning = 5;
    private const int MaxValidationWarningCharacters = 512;

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
                cancellationToken,
                out var skippedCount);
            cancellationToken.ThrowIfCancellationRequested();
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
                cancellationToken,
                out var skippedCount);
            cancellationToken.ThrowIfCancellationRequested();
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
        catch (Exception ex) when (ex is not OutOfMemoryException)
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

    private ActiveRule[] BuildSnapshot(
        RuleLoadResult load,
        CancellationToken cancellationToken,
        out int skippedCount)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rules = load.Rules;
        var snapshot = new List<ActiveRule>(rules.Count);
        var rejectedRuleCount = load.RejectedSources.Count;
        var detailedWarningCount = 0;

        foreach (var sourceRejection in load.RejectedSources)
        {
            if (detailedWarningCount >= MaxDetailedInvalidRuleWarningsPerLoad)
            {
                break;
            }

            LogRejectedRule(new RuleRejection(
                sourceRejection.RuleId,
                sourceRejection.Reason,
                sourceRejection.Exception));
            detailedWarningCount++;
        }

        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var captureRejection =
                detailedWarningCount < MaxDetailedInvalidRuleWarningsPerLoad;
            if (TryBuildRuleSnapshot(
                rule,
                captureRejection,
                out var activeRule,
                out var rejection))
            {
                snapshot.Add(activeRule);
            }
            else
            {
                rejectedRuleCount++;
                if (rejection is not null)
                {
                    LogRejectedRule(rejection);
                    detailedWarningCount++;
                }
            }
        }

        if (load.SourceRuleCount > 0 && snapshot.Count == 0)
        {
            _logger.LogWarning(
                "Active-rule load returned {RuleCount} rules, but none passed validation. Logged details for {DetailedWarningCount} rules and suppressed {SuppressedWarningCount}; refusing to publish an empty candidate snapshot.",
                load.SourceRuleCount,
                detailedWarningCount,
                Math.Max(0, rejectedRuleCount - detailedWarningCount));
            throw new InvalidDataException(
                "The active-rule load was non-empty, but none of its rules passed validation.");
        }

        if (rejectedRuleCount > 0)
        {
            _logger.LogWarning(
                "Active-rule load accepted {AcceptedRuleCount} rules and skipped {RejectedRuleCount} invalid rules. Logged details for {DetailedWarningCount} rules and suppressed {SuppressedWarningCount}.",
                snapshot.Count,
                rejectedRuleCount,
                detailedWarningCount,
                Math.Max(0, rejectedRuleCount - detailedWarningCount));
        }

        cancellationToken.ThrowIfCancellationRequested();
        skippedCount = rejectedRuleCount;
        return snapshot.ToArray();
    }

    private bool TryBuildRuleSnapshot(
        RuleDto? rule,
        bool captureRejection,
        out ActiveRule activeRule,
        out RuleRejection? rejection)
    {
        activeRule = null!;
        rejection = null;
        if (rule is null)
        {
            if (captureRejection)
            {
                rejection = new RuleRejection(
                    "<null>",
                    "The rule repository returned a null rule.",
                    null);
            }

            return false;
        }

        var validationResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(
            rule,
            new ValidationContext(rule),
            validationResults,
            validateAllProperties: true))
        {
            if (captureRejection)
            {
                rejection = new RuleRejection(
                    RuleLabel(rule),
                    BuildValidationFailure(validationResults),
                    null);
            }

            return false;
        }

        Geometry geometry;
        try
        {
            geometry = _geometry.ReadRuleGeometry(rule);
        }
        catch (Exception ex) when (
            ex is ParseException or
                ArgumentException or
                GeometryValidationException or
                GatewayValidationException)
        {
            if (captureRejection)
            {
                rejection = new RuleRejection(
                    RuleLabel(rule),
                    "Its gateway snapshot could not be built.",
                    ex);
            }

            return false;
        }

        activeRule = BuildRuleSnapshot(rule, geometry);
        return true;
    }

    private void LogRejectedRule(RuleRejection rejection)
    {
        if (rejection.Exception is null)
        {
            _logger.LogWarning(
                "Skipping invalid active rule {RuleId}. Reason: {ValidationFailure}",
                rejection.RuleId,
                rejection.Reason);
            return;
        }

        _logger.LogWarning(
            rejection.Exception,
            "Skipping invalid active rule {RuleId}. Reason: {ValidationFailure}",
            rejection.RuleId,
            rejection.Reason);
    }

    private static string BuildValidationFailure(
        IReadOnlyList<ValidationResult> validationResults)
    {
        var validationFailure = string.Join(
            " | ",
            validationResults
                .Take(MaxValidationErrorsPerRuleWarning)
                .Select(result => result.ErrorMessage ?? "Rule validation failed."));

        var omittedErrorCount =
            validationResults.Count - MaxValidationErrorsPerRuleWarning;
        if (omittedErrorCount > 0)
        {
            validationFailure += $" | {omittedErrorCount} more validation errors";
        }

        return validationFailure.Length <= MaxValidationWarningCharacters
            ? validationFailure
            : string.Concat(
                validationFailure.AsSpan(0, MaxValidationWarningCharacters - 3),
                "...");
    }

    private static string RuleLabel(RuleDto rule) =>
        !string.IsNullOrWhiteSpace(rule.Id)
            ? rule.Id
            : !string.IsNullOrWhiteSpace(rule.RuleName)
                ? rule.RuleName
                : "<unknown>";

    private static Activity? StartRefreshActivity(string refreshType)
    {
        var activity = TelemetrySources.Gateway.StartActivity(
            "gateway.rule_cache.refresh",
            ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "gateway");
            activity.SetTag("imaging_pipeline.gateway.rule_cache.refresh.type", refreshType);
        }

        return activity;
    }

    private static ActiveRule BuildRuleSnapshot(
        RuleDto rule,
        Geometry geometry) =>
        new(
            rule.Id,
            rule.AlgorithmNames.ToArray(),
            BuildSensorSnapshot(rule.Sensors),
            BuildTenantSnapshot(rule.TenantsInfo),
            rule.MinimumResolution,
            rule.MaximumResolution,
            geometry);

    private static IReadOnlyDictionary<string, int> BuildSensorSnapshot(
        IReadOnlyDictionary<string, List<RegistrationQuality>>? sensors)
    {
        if (sensors is null)
        {
            throw new InvalidDataException("Rule sensors must not be null.");
        }

        if (sensors.Count == 0)
        {
            return new Dictionary<string, int>(0, StringComparer.Ordinal);
        }

        var snapshot = new Dictionary<string, int>(sensors.Count, StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
            if (sensor.Value is null || sensor.Value.Count == 0)
            {
                throw new InvalidDataException(
                    $"Rule sensor '{sensor.Key}' must contain at least one registration quality.");
            }

            snapshot[sensor.Key] = RegistrationQualityMask.From(sensor.Value);
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

    private sealed record RuleRejection(
        string RuleId,
        string Reason,
        Exception? Exception);

    public void Dispose()
    {
        _refreshCancellation?.Dispose();
    }
}
