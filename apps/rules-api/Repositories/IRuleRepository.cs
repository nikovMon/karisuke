using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Rules.Api.Repositories;

public interface IRuleRepository
{
    Task<IReadOnlyList<RuleDto>> GetAllAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default);
    Task<RuleDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<RuleDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default);
    Task<bool> ExistsByNameAsync(string ruleName, string? excludingId = null, CancellationToken cancellationToken = default);
    Task SaveAsync(RuleDto rule, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
}
