using System.Collections.Frozen;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.UnifiedGateway.Rules;

/// <summary>The active rules of one pipeline, as of one successful load.</summary>
public sealed record PipelineRules(PipelineDefinition Pipeline, IReadOnlyList<ActiveRule> Rules);

/// <summary>
/// Holds the active rules of every enabled pipeline and reloads them in the background. Each
/// pipeline loads on its own: a failed reload keeps that pipeline's previous rules and never
/// affects the others. Startup fails unless every enabled pipeline loads, so a running gateway
/// never matches against a missing rule set.
/// </summary>
public sealed class GatewayRuleCache : BackgroundService
{
    private readonly IReadOnlyList<RuleSource> _sources;
    private readonly IRuleRepository _repository;
    private readonly ILogger<GatewayRuleCache> _logger;
    private readonly RuleRefreshOptions _refresh;
    private readonly FrozenDictionary<string, (PipelineDefinition Pipeline, IPipelineContract Contract)> _pipelines;
    private FrozenDictionary<string, PipelineRules> _current = FrozenDictionary<string, PipelineRules>.Empty;

    public GatewayRuleCache(
        IPipelineCatalog catalog,
        IRuleSourceResolver sources,
        IPipelineContractRegistry contracts,
        IRuleRepository repository,
        IOptions<RuleRefreshOptions> refresh,
        ILogger<GatewayRuleCache> logger)
    {
        var enabled = catalog.GetEnabled();
        _sources = enabled.Select(pipeline => sources.Resolve(pipeline.PipelineId)).ToArray();
        _pipelines = enabled.ToFrozenDictionary(
            pipeline => pipeline.PipelineId,
            pipeline => (pipeline, contracts.GetRequired(pipeline.ContractId)),
            StringComparer.Ordinal);
        _repository = repository;
        _refresh = refresh.Value;
        _logger = logger;
    }

    /// <summary>The latest rules of every enabled pipeline.</summary>
    public IReadOnlyCollection<PipelineRules> Current => Volatile.Read(ref _current).Values;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var source in _sources)
        {
            if (!await TryLoadAsync(source, RuleLoadKind.Startup, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"Rules for pipeline '{source.PipelineId}' could not be loaded at startup; the failure is logged.");
            }
        }

        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(NextRefreshDelay(), stoppingToken);
            foreach (var source in _sources)
            {
                await TryLoadAsync(source, RuleLoadKind.Refresh, stoppingToken);
            }
        }
    }

    /// <summary>
    /// Loads one pipeline's rules and publishes them. On failure the failure is logged, the
    /// pipeline keeps its previous rules, and false is returned.
    /// </summary>
    private async Task<bool> TryLoadAsync(RuleSource source, RuleLoadKind kind, CancellationToken cancellationToken)
    {
        var (pipeline, contract) = _pipelines[source.PipelineId];
        using var telemetry = RuleLoadTelemetry.Begin(pipeline.PipelineId, kind, _logger);
        try
        {
            var load = await _repository.GetActiveRulesAsync(source.IndexName, cancellationToken);
            var snapshot = RuleSnapshotBuilder.Build(load, contract);
            telemetry.Built(snapshot);
            if (snapshot.AllRulesRejected)
            {
                throw new InvalidDataException("Every loaded rule was rejected.");
            }

            Publish(new PipelineRules(pipeline, snapshot.Rules));
            return true;
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            telemetry.Failed(ex, RetainedRuleCount(pipeline.PipelineId));
            return false;
        }
    }

    // Loads run one at a time, so replacing the whole map cannot lose a concurrent update.
    private void Publish(PipelineRules rules)
    {
        var next = new Dictionary<string, PipelineRules>(_current, StringComparer.Ordinal)
        {
            [rules.Pipeline.PipelineId] = rules
        };
        Volatile.Write(ref _current, next.ToFrozenDictionary(StringComparer.Ordinal));
    }

    private int RetainedRuleCount(string pipelineId) =>
        Volatile.Read(ref _current).TryGetValue(pipelineId, out var rules) ? rules.Rules.Count : 0;

    private TimeSpan NextRefreshDelay() =>
        TimeSpan.FromSeconds(_refresh.IntervalSeconds + Random.Shared.NextDouble() * _refresh.JitterSeconds);
}

public enum RuleLoadKind
{
    Startup,
    Refresh
}
