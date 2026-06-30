using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.Repositories;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Services;

public sealed class RuleService : IRuleService
{
    private readonly IRuleRepository _repository;
    private readonly ILogger<RuleService> _logger;

    public RuleService(IRuleRepository repository, ILogger<RuleService> logger)
    {
        _repository = repository;
        _logger = logger;
    }

    public Task<IReadOnlyList<RuleConfigDto>> GetRulesAsync(
        bool isNameOnly,
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        return _repository.GetAllAsync(isActive, cancellationToken);
    }

    public async Task<IReadOnlyList<string>> GetRuleNamesAsync(
        bool? isActive,
        CancellationToken cancellationToken = default)
    {
        var rules = await _repository.GetAllAsync(isActive, cancellationToken);
        return rules.Select(rule => rule.RuleName).ToArray();
    }

    public Task<RuleConfigDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        _repository.GetByIdAsync(id, cancellationToken);

    public Task<RuleConfigDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default) =>
        _repository.GetByNameAsync(ruleName, cancellationToken);

    public async Task<RuleOperationResult<RuleConfigDto>> CreateAsync(
        RuleConfigDto rule,
        CancellationToken cancellationToken = default)
    {
        rule.Id = string.Empty;
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}; RuleName: {RuleName}",
            "create",
            rule.Id,
            rule.RuleName);

        var errors = RuleValidation.ValidateRule(rule);
        if (errors.Count > 0)
        {
            LogValidationFailure("create", rule.Id, errors);
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", errors));
        }

        if (await _repository.ExistsByNameAsync(rule.RuleName, cancellationToken: cancellationToken))
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because ruleName {RuleName} already exists. RuleId: {RuleId}",
                "create",
                rule.RuleName,
                rule.Id);
            return RuleOperationResult<RuleConfigDto>.Conflict($"Rule with ruleName '{rule.RuleName}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        rule.CreationTime = now;
        rule.UpdateTime = now;
        NormalizeCollections(rule);

        await _repository.SaveAsync(rule, cancellationToken);
        _logger.LogInformation(
            "Rule operation {Operation} succeeded. RuleId: {RuleId}; RuleName: {RuleName}",
            "create",
            rule.Id,
            rule.RuleName);
        return RuleOperationResult<RuleConfigDto>.Success(rule);
    }

    public Task<RuleOperationResult<RuleConfigDto>> UpdateAsync(
        string id,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateCoreAsync(id, request, "update", cancellationToken);

    private async Task<RuleOperationResult<RuleConfigDto>> UpdateCoreAsync(
        string id,
        UpdateRuleRequest request,
        string operation,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}; UpdatedFields: {UpdatedFields}",
            operation,
            id,
            string.Join(", ", request.ProvidedFields.Order(StringComparer.Ordinal)));

        var validationErrors = RuleValidation.ValidateUpdate(request);
        if (validationErrors.Count > 0)
        {
            LogValidationFailure(operation, id, validationErrors);
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", validationErrors));
        }

        var rule = await _repository.GetByIdAsync(id, cancellationToken);
        if (rule is null)
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because the rule was not found. RuleId: {RuleId}",
                operation,
                id);
            return RuleOperationResult<RuleConfigDto>.NotFound($"Rule '{id}' was not found.");
        }

        if (request.HasField("ruleName") &&
            !string.Equals(rule.RuleName, request.RuleName, StringComparison.Ordinal) &&
            await _repository.ExistsByNameAsync(request.RuleName!, id, cancellationToken))
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because ruleName {RuleName} already exists. RuleId: {RuleId}",
                operation,
                request.RuleName,
                id);
            return RuleOperationResult<RuleConfigDto>.Conflict($"Rule with ruleName '{request.RuleName}' already exists.");
        }

        ApplyUpdate(rule, request);
        var ruleErrors = RuleValidation.ValidateRule(rule);
        if (ruleErrors.Count > 0)
        {
            LogValidationFailure(operation, id, ruleErrors);
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", ruleErrors));
        }

        await _repository.SaveAsync(rule, cancellationToken);
        _logger.LogInformation(
            "Rule operation {Operation} succeeded. RuleId: {RuleId}; UpdatedFieldCount: {UpdatedFieldCount}",
            operation,
            id,
            request.ProvidedFields.Count);
        return RuleOperationResult<RuleConfigDto>.Success(rule);
    }

    public async Task<RuleOperationResult<BulkOperationResult>> UpdateBulkAsync(
        IReadOnlyCollection<string> ids,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting rule operation {Operation}. RequestedCount: {RequestedCount}; UpdatedFields: {UpdatedFields}",
            "bulk_update",
            ids.Count,
            string.Join(", ", request.ProvidedFields.Order(StringComparer.Ordinal)));

        var errors = RuleValidation.ValidateIds(ids)
            .Concat(RuleValidation.ValidateUpdate(request))
            .ToArray();

        if (errors.Length > 0)
        {
            LogValidationFailure("bulk_update", null, errors);
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" ", errors));
        }

        if (request.HasField("ruleName") && ids.Count > 1)
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because one ruleName cannot be assigned to multiple rules. RuleCount: {RuleCount}",
                "bulk_update",
                ids.Count);
            return RuleOperationResult<BulkOperationResult>.Conflict("Cannot set the same ruleName on multiple rules.");
        }

        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var updated = await UpdateCoreAsync(id, request, "bulk_update_item", cancellationToken);
            AddBulkResult(result, id, updated.Status, updated.Error);
        }

        LogBulkOutcome("bulk_update", ids.Count, result);
        return RuleOperationResult<BulkOperationResult>.Success(result);
    }

    public async Task<RuleOperationResult> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}",
            "delete",
            id);

        var deleted = await _repository.DeleteAsync(id, cancellationToken);
        if (deleted)
        {
            _logger.LogInformation(
                "Rule operation {Operation} succeeded. RuleId: {RuleId}",
                "delete",
                id);
            return RuleOperationResult.Success();
        }

        _logger.LogWarning(
            "Rule operation {Operation} failed because the rule was not found. RuleId: {RuleId}",
            "delete",
            id);
        return RuleOperationResult.NotFound($"Rule '{id}' was not found.");
    }

    public async Task<RuleOperationResult<RuleConfigDto>> ChangeActivityAsync(
        string id,
        ChangeRuleActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!request.IsActive.HasValue)
        {
            const string error = "isActive is required.";
            LogValidationFailure("change_activity", id, [error]);
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(error);
        }

        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}; IsActive: {IsActive}",
            "change_activity",
            id,
            request.IsActive);

        var update = new UpdateRuleRequest
        {
            IsActive = request.IsActive.Value
        };
        update.ProvidedFields.Add("isActive");
        return await UpdateCoreAsync(id, update, "change_activity", cancellationToken);
    }

    public Task<RuleOperationResult<BulkOperationResult>> AddSensorsAsync(
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return MutateSensorsAsync(ids, request, add: true, cancellationToken);
    }

    public Task<RuleOperationResult<BulkOperationResult>> RemoveSensorsAsync(
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default)
    {
        return MutateSensorsAsync(ids, request, add: false, cancellationToken);
    }

    private async Task<RuleOperationResult<BulkOperationResult>> MutateSensorsAsync(
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        bool add,
        CancellationToken cancellationToken)
    {
        var operation = add ? "add_sensors" : "remove_sensors";
        _logger.LogDebug(
            "Starting rule operation {Operation}. RequestedCount: {RequestedCount}; SensorName: {SensorName}; SensorValueCount: {SensorValueCount}",
            operation,
            ids.Count,
            request.SensorName,
            request.Values?.Count ?? 0);

        var errors = RuleValidation.ValidateIds(ids)
            .Concat(RuleValidation.ValidateSensorRequest(request))
            .ToArray();

        if (errors.Length > 0)
        {
            LogValidationFailure(operation, null, errors);
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" ", errors));
        }

        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var rule = await _repository.GetByIdAsync(id, cancellationToken);
            if (rule is null)
            {
                _logger.LogWarning(
                    "Rule operation {Operation} skipped a missing rule. RuleId: {RuleId}; SensorName: {SensorName}",
                    operation,
                    id,
                    request.SensorName);
                result.FailedIds.Add(new BulkOperationFailure(id, "Rule not found"));
                continue;
            }

            if (add)
            {
                AddSensorValues(rule, request);
            }
            else
            {
                RemoveSensorValues(rule, request);
            }

            rule.UpdateTime = DateTimeOffset.UtcNow;
            NormalizeCollections(rule);
            await _repository.SaveAsync(rule, cancellationToken);
            result.SuccessIds.Add(id);
        }

        LogBulkOutcome(operation, ids.Count, result, request.SensorName);
        return RuleOperationResult<BulkOperationResult>.Success(result);
    }

    private static void ApplyUpdate(RuleConfigDto rule, UpdateRuleRequest request)
    {
        if (request.HasField("ruleName"))
        {
            rule.RuleName = request.RuleName ?? string.Empty;
        }

        if (request.HasField("description"))
        {
            rule.Description = request.Description;
        }

        if (request.HasField("algorithmName"))
        {
            rule.AlgorithmName = request.AlgorithmName;
        }

        if (request.HasField("sensors"))
        {
            rule.Sensors = request.Sensors ?? new Dictionary<string, List<string>>(StringComparer.Ordinal);
        }

        if (request.HasField("isActive"))
        {
            rule.IsActive = request.IsActive.GetValueOrDefault();
        }

        if (request.HasField("tenantsInfo"))
        {
            rule.TenantsInfo = request.TenantsInfo ?? [];
        }

        if (request.HasField("minimumResolution"))
        {
            rule.MinimumResolution = request.MinimumResolution.GetValueOrDefault();
        }

        if (request.HasField("maximumResolution"))
        {
            rule.MaximumResolution = request.MaximumResolution ?? 999;
        }

        if (request.HasField("area"))
        {
            rule.Area = request.Area ?? string.Empty;
        }

        if (request.HasField("locationWkt"))
        {
            rule.LocationWkt = request.LocationWkt;
        }

        if (request.HasField("locationGeoJson"))
        {
            rule.LocationGeoJson = request.LocationGeoJson;
        }

        if (request.HasField("isPhotoOld"))
        {
            rule.IsPhotoOld = request.IsPhotoOld;
        }

        rule.UpdateTime = DateTimeOffset.UtcNow;
        NormalizeCollections(rule);
    }

    private static void AddSensorValues(RuleConfigDto rule, RuleSensorUpdateRequest request)
    {
        if (!rule.Sensors.TryGetValue(request.SensorName, out var values))
        {
            rule.Sensors[request.SensorName] = request.Values.Distinct(StringComparer.Ordinal).ToList();
            return;
        }

        foreach (var value in request.Values)
        {
            if (!values.Contains(value, StringComparer.Ordinal))
            {
                values.Add(value);
            }
        }
    }

    private static void RemoveSensorValues(RuleConfigDto rule, RuleSensorUpdateRequest request)
    {
        if (!rule.Sensors.TryGetValue(request.SensorName, out var values))
        {
            return;
        }

        values.RemoveAll(value => request.Values.Contains(value, StringComparer.Ordinal));
        if (values.Count == 0)
        {
            rule.Sensors.Remove(request.SensorName);
        }
    }

    private static void NormalizeCollections(RuleConfigDto rule)
    {
        rule.Sensors = new Dictionary<string, List<string>>(
            (rule.Sensors ?? new Dictionary<string, List<string>>(StringComparer.Ordinal))
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => new KeyValuePair<string, List<string>>(
                    item.Key,
                    (item.Value ?? [])
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .Distinct(StringComparer.Ordinal)
                        .ToList())),
            StringComparer.Ordinal);
        rule.TenantsInfo ??= [];
    }

    private static void AddBulkResult(
        BulkOperationResult result,
        string id,
        RuleOperationStatus status,
        string? error)
    {
        if (status == RuleOperationStatus.Success)
        {
            result.SuccessIds.Add(id);
            return;
        }

        result.FailedIds.Add(new BulkOperationFailure(id, error ?? "Rule update failed"));
    }

    private void LogValidationFailure(
        string operation,
        string? ruleId,
        IReadOnlyCollection<string> errors)
    {
        _logger.LogWarning(
            "Rule operation {Operation} failed validation. RuleId: {RuleId}; ErrorCount: {ErrorCount}; ValidationErrors: {ValidationErrors}",
            operation,
            ruleId,
            errors.Count,
            string.Join(" | ", errors));
    }

    private void LogBulkOutcome(
        string operation,
        int requestedCount,
        BulkOperationResult result,
        string? sensorName = null)
    {
        var level = result.FailedIds.Count > 0 ? LogLevel.Warning : LogLevel.Information;
        _logger.Log(
            level,
            "Rule operation {Operation} completed. RequestedCount: {RequestedCount}; SuccessCount: {SuccessCount}; FailureCount: {FailureCount}; SensorName: {SensorName}",
            operation,
            requestedCount,
            result.SuccessIds.Count,
            result.FailedIds.Count,
            sensorName);
    }
}
