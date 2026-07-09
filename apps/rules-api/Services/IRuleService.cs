using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.Rules.Api.Repositories;

namespace ImagingPipeline.Rules.Api.Services;

internal interface IRuleService
{
    IRuleRepository Repository { get; }

    Task<IReadOnlyList<RuleDto>> GetRulesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> GetRuleNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default);

    Task<RuleDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<RuleDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default);

    Task<RuleOperationResult<RuleDto>> CreateAsync(CreateRuleRequest request, CancellationToken cancellationToken = default);

    Task<RuleOperationResult<RuleDto>> UpdateAsync(string id, UpdateRuleRequest request, CancellationToken cancellationToken = default);

    Task<RuleOperationResult<BulkOperationResult>> UpdateBulkAsync(
        IReadOnlyCollection<string> ids,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default);

    Task<RuleOperationResult> DeleteAsync(string id, CancellationToken cancellationToken = default);

    Task<RuleOperationResult<RuleDto>> ChangeActivityAsync(string id, ChangeRuleActivationStatusRequest request, CancellationToken cancellationToken = default);

    Task<RuleOperationResult<BulkOperationResult>> AddSensorsAsync(
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default);

    Task<RuleOperationResult<BulkOperationResult>> RemoveSensorsAsync(
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default);
}
