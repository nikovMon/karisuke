using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Processing.Rules;

public sealed class ActiveRuleCache : IHostedService, IDisposable
{
    private readonly IRuleRepository _repository;
    private readonly GatewayGeometryConverter _geometry;
    private readonly GatewayHealthState _healthState;
    private readonly TimeSpan _refreshInterval;
    private readonly TimeSpan _refreshJitter;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;
    private IReadOnlyList<ActiveRule> _current = [];

    public ActiveRuleCache(
        IRuleRepository repository,
        GatewayGeometryConverter geometry,
        IOptions<GatewaySettings> settings,
        GatewayHealthState healthState)
    {
        _repository = repository;
        _geometry = geometry;
        _healthState = healthState;
        _refreshInterval = TimeSpan.FromSeconds(settings.Value.RuleRefreshIntervalSeconds);
        _refreshJitter = TimeSpan.FromSeconds(settings.Value.RuleRefreshJitterSeconds);
    }

    public IReadOnlyList<ActiveRule> Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var initialRules = BuildSnapshot(await _repository.GetActiveRulesAsync(cancellationToken));
        Volatile.Write(ref _current, initialRules);
        _healthState.MarkRulesRefreshSucceeded();

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
        try
        {
            var rules = BuildSnapshot(await _repository.GetActiveRulesAsync(cancellationToken));
            Volatile.Write(ref _current, rules);
            _healthState.MarkRulesRefreshSucceeded();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep the last valid snapshot when a refresh fails.
            _healthState.MarkRulesRefreshFailed();
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

    private ActiveRule[] BuildSnapshot(IReadOnlyList<RuleDto> rules)
    {
        var snapshot = new List<ActiveRule>(rules.Count);
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
            catch
            {
                // TODO: Log a warning with the skipped rule id and exception details once cache logging is wired.
            }
        }

        return snapshot.ToArray();
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

    private static IReadOnlyDictionary<string, int> BuildSensorSnapshot(
        IReadOnlyDictionary<string, List<RegistrationQuality>>? sensors)
    {
        if (sensors is null || sensors.Count == 0)
        {
            return new Dictionary<string, int>(0, StringComparer.Ordinal);
        }

        var snapshot = new Dictionary<string, int>(sensors.Count, StringComparer.Ordinal);
        foreach (var sensor in sensors)
        {
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

    public void Dispose()
    {
        _refreshCancellation?.Dispose();
    }
}
