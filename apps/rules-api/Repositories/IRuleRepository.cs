using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Rules.Api.Repositories;

public interface IRuleRepository
{
    Task<IReadOnlyList<RuleConfigDto>> GetAllAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default);
    Task<RuleConfigDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<RuleConfigDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default);
    Task<bool> ExistsByNameAsync(string ruleName, string? excludingId = null, CancellationToken cancellationToken = default);
    Task SaveAsync(RuleConfigDto rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
