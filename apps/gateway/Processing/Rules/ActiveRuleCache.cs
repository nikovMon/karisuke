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
        using var timer = new PeriodicTimer(_refreshInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
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

    private ActiveRule[] BuildSnapshot(IReadOnlyList<RuleConfigDto> rules) =>
        rules
            .Select(rule => new ActiveRule(rule, _geometry.ReadRuleGeometry(rule)))
            .ToArray();

    public void Dispose()
    {
        _refreshCancellation?.Dispose();
    }
}
