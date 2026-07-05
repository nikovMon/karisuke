using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Processing.Rules;

public interface IRuleRepository
{
    Task<IReadOnlyList<RuleConfigDto>> GetActiveRulesAsync(CancellationToken cancellationToken);
}
