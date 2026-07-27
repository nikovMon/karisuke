using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Processing.Rules;

public interface IRuleRepository
{
    Task<RuleLoadResult> GetActiveRulesAsync(CancellationToken cancellationToken);
}

public sealed record RuleLoadResult(
    IReadOnlyList<RuleDto> Rules,
    IReadOnlyList<RuleSourceRejection> RejectedSources)
{
    public int SourceRuleCount => checked(Rules.Count + RejectedSources.Count);

    public static RuleLoadResult FromRules(IReadOnlyList<RuleDto> rules) =>
        new(rules, []);
}

public sealed record RuleSourceRejection(
    string RuleId,
    string Reason,
    Exception? Exception = null);
