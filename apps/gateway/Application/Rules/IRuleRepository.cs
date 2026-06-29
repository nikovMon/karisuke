using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Gateway.Application.Rules;

public interface IRuleRepository
{
    Task<IReadOnlyList<RuleConfigDto>> GetActiveRulesAsync(CancellationToken cancellationToken);
}
