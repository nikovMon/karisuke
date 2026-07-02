using ImagingPipeline.Rules.Api.Repositories;
using ImagingPipeline.Common.Dtos.Rules.Models;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class ThrowingRuleRepository : IRuleRepository
{
    public Task<IReadOnlyList<RuleDto>> GetAllAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
    }

    public Task<IReadOnlyList<string>> GetNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
    }

    public Task<RuleDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<RuleDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
    }

    public Task SaveAsync(RuleDto rule, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");

    public Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default) =>
        throw new RuleRepositoryException("Sensitive Elasticsearch failure details.");
}
