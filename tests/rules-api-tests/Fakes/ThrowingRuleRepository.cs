using ImagingPipeline.Rules.Api.Repositories;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class ThrowingRuleRepository : IRuleRepository
{
    public Task<IReadOnlyList<RuleConfigDto>> GetAllAsync(
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
    }

    public Task<RuleConfigDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<RuleConfigDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
    }

    public Task SaveAsync(RuleConfigDto rule, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
}
