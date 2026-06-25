using ImagingPipeline.Rules.Contracts.Models;
using ImagingPipeline.Rules.Contracts.Requests;
using ImagingPipeline.Rules.Contracts.Responses;

namespace ImagingPipeline.Rules.Api.Services;

public interface IRuleService
{
    Task<IReadOnlyList<RuleConfigDto>> GetRulesAsync(bool isNameOnly, bool? isActive, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<string>> GetRuleNamesAsync(bool? isActive, CancellationToken cancellationToken = default);
    Task<RuleConfigDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);
    Task<RuleConfigDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<RuleConfigDto>> CreateAsync(RuleConfigDto rule, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<RuleConfigDto>> UpdateAsync(string id, UpdateRuleRequest request, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<BulkOperationResult>> UpdateBulkAsync(IReadOnlyCollection<string> ids, UpdateRuleRequest request, CancellationToken cancellationToken = default);
    Task<RuleOperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<RuleConfigDto>> ChangeActivityAsync(string id, ChangeRuleActivityRequest request, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<BulkOperationResult>> AddSensorsAsync(IReadOnlyCollection<string> ids, RuleSensorUpdateRequest request, CancellationToken cancellationToken = default);
    Task<RuleOperationResult<BulkOperationResult>> RemoveSensorsAsync(IReadOnlyCollection<string> ids, RuleSensorUpdateRequest request, CancellationToken cancellationToken = default);
}
