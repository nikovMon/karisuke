using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Application.Rules;

public sealed class RuleCache : IHostedService, IDisposable
{
    private readonly IRuleRepository _repository;
    private readonly GatewayHealthState _healthState;
    private readonly TimeSpan _refreshInterval;
    private CancellationTokenSource? _refreshCancellation;
    private Task? _refreshTask;
    private IReadOnlyList<RuleConfigDto> _current = [];

    public RuleCache(
        IRuleRepository repository,
        IOptions<ElasticsearchSettings> settings,
        GatewayHealthState healthState)
    {
        _repository = repository;
        _healthState = healthState;
        _refreshInterval = TimeSpan.FromSeconds(settings.Value.RefreshIntervalSeconds);
    }

    public IReadOnlyList<RuleConfigDto> Current => Volatile.Read(ref _current);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var initialRules = await _repository.GetActiveRulesAsync(cancellationToken);
        Volatile.Write(ref _current, initialRules);
        _healthState.MarkRulesLoaded();

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
            var rules = await _repository.GetActiveRulesAsync(cancellationToken);
            Volatile.Write(ref _current, rules);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Keep the last valid snapshot. Logging is intentionally omitted for now.
        }
    }

    public void Dispose()
    {
        _refreshCancellation?.Dispose();
    }
}
