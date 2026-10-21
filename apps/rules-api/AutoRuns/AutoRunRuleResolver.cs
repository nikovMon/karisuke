using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Historical;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.Rules.Api.Services;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed record AutoRunResolvedTarget(
    AutoRunTarget Target, PipelineRuleDocument Rule, ActiveRule MatchRule, IPipelineContract Contract);

public interface IAutoRunRuleResolver
{
    Task<IReadOnlyList<AutoRunResolvedTarget>> ResolveAsync(
        IReadOnlyList<AutoRunTarget> targets, CancellationToken cancellationToken);
}

public sealed class AutoRunRuleResolver(
    PipelineRuleService rules, IPipelineCatalog catalog, IPipelineContractRegistry contracts) : IAutoRunRuleResolver
{
    public async Task<IReadOnlyList<AutoRunResolvedTarget>> ResolveAsync(
        IReadOnlyList<AutoRunTarget> targets, CancellationToken cancellationToken)
    {
        var result = new List<AutoRunResolvedTarget>();
        foreach (var target in targets)
        {
            if (!catalog.TryGet(target.PipelineId, out var pipeline))
                throw new AutoRunRequestException(400, $"Unknown pipeline '{target.PipelineId}'.");
            if (!pipeline.Enabled)
                throw new AutoRunRequestException(400, $"Pipeline '{target.PipelineId}' is disabled.");
            var original = await rules.GetByIdAsync(target.PipelineId, target.RuleId, cancellationToken)
                ?? throw new AutoRunRequestException(400, $"Rule '{target.RuleId}' was not found in pipeline '{target.PipelineId}'.");
            // Serialize once at the boundary to own every nested list and JsonElement.
            var rule = JsonSerializer.Deserialize<PipelineRuleDocument>(
                JsonSerializer.SerializeToUtf8Bytes(original, AutoRunJson.Options), AutoRunJson.Options)!;
            var contract = contracts.GetRequired(pipeline.ContractId);
            try
            {
                var matchRule = HistoricalRuleSnapshot.Build(rule, contract);
                result.Add(new(target, rule, matchRule, contract));
            }
            catch (ArgumentException ex)
            {
                throw new AutoRunRequestException(400, $"Rule '{target.RuleId}' is invalid: {ex.Message}");
            }
        }
        return result;
    }
}
