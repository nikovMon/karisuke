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
        var errors = RuleValidation.ValidateRule(rule);
        if (errors.Count > 0)
        {
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", errors));
        }

        if (await _repository.ExistsByNameAsync(rule.RuleName, cancellationToken: cancellationToken))
        {
            return RuleOperationResult<RuleConfigDto>.Conflict($"Rule with ruleName '{rule.RuleName}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        rule.CreatedAt = now;
        rule.ModifiedAt = now;
        NormalizeCollections(rule);

        await _repository.SaveAsync(rule, cancellationToken);
        _logger.LogInformation("Created rule {RuleId}", rule.Id);
        return RuleOperationResult<RuleConfigDto>.Success(rule);
    }

    public async Task<RuleOperationResult<RuleConfigDto>> UpdateAsync(
        string id,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var validationErrors = RuleValidation.ValidateUpdate(request);
        if (validationErrors.Count > 0)
        {
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", validationErrors));
        }

        var rule = await _repository.GetByIdAsync(id, cancellationToken);
        if (rule is null)
        {
            return RuleOperationResult<RuleConfigDto>.NotFound($"Rule '{id}' was not found.");
        }

        if (request.HasField("ruleName") &&
            !string.Equals(rule.RuleName, request.RuleName, StringComparison.Ordinal) &&
            await _repository.ExistsByNameAsync(request.RuleName!, id, cancellationToken))
        {
            return RuleOperationResult<RuleConfigDto>.Conflict($"Rule with ruleName '{request.RuleName}' already exists.");
        }

        ApplyUpdate(rule, request);
        var ruleErrors = RuleValidation.ValidateRule(rule);
        if (ruleErrors.Count > 0)
        {
            return RuleOperationResult<RuleConfigDto>.ValidationFailed(string.Join(" ", ruleErrors));
        }

        await _repository.SaveAsync(rule, cancellationToken);
        _logger.LogInformation("Updated rule {RuleId}", id);
        return RuleOperationResult<RuleConfigDto>.Success(rule);
    }

    public async Task<RuleOperationResult<BulkOperationResult>> UpdateBulkAsync(
        IReadOnlyCollection<string> ids,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var errors = RuleValidation.ValidateIds(ids)
            .Concat(RuleValidation.ValidateUpdate(request))
            .ToArray();

        if (errors.Length > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" ", errors));
        }

        if (request.HasField("ruleName") && ids.Count > 1)
        {
            return RuleOperationResult<BulkOperationResult>.Conflict("Cannot set the same ruleName on multiple rules.");
        }

        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var updated = await UpdateAsync(id, request, cancellationToken);
            AddBulkResult(result, id, updated.Status, updated.Error);
        }

        return RuleOperationResult<BulkOperationResult>.Success(result);
    }

    public async Task<RuleOperationResult> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        var deleted = await _repository.DeleteAsync(id, cancellationToken);
        return deleted
            ? RuleOperationResult.Success()
            : RuleOperationResult.NotFound($"Rule '{id}' was not found.");
    }

    public async Task<RuleOperationResult<RuleConfigDto>> ChangeActivityAsync(
        string id,
        ChangeRuleActivityRequest request,
        CancellationToken cancellationToken = default)
    {
        var update = new UpdateRuleRequest
        {
            IsActive = request.IsActive
        };
        update.ProvidedFields.Add("isActive");
        return await UpdateAsync(id, update, cancellationToken);
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
        var errors = RuleValidation.ValidateIds(ids)
            .Concat(RuleValidation.ValidateSensorRequest(request))
            .ToArray();

        if (errors.Length > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" ", errors));
        }

        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var rule = await _repository.GetByIdAsync(id, cancellationToken);
            if (rule is null)
            {
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

            rule.ModifiedAt = DateTimeOffset.UtcNow;
            NormalizeCollections(rule);
            await _repository.SaveAsync(rule, cancellationToken);
            result.SuccessIds.Add(id);
        }

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

        if (request.HasField("tenants"))
        {
            rule.Tenants = request.Tenants ?? [];
        }

        if (request.HasField("minResolution"))
        {
            rule.MinResolution = request.MinResolution.GetValueOrDefault();
        }

        if (request.HasField("maxResolution"))
        {
            rule.MaxResolution = request.MaxResolution.GetValueOrDefault();
        }

        if (request.HasField("area"))
        {
            rule.Area = request.Area ?? string.Empty;
        }

        if (request.HasField("wkt"))
        {
            rule.Wkt = request.Wkt;
        }

        if (request.HasField("geoJson"))
        {
            rule.GeoJson = request.GeoJson;
        }

        if (request.HasField("maxLookBackDay"))
        {
            rule.MaxLookBackDay = request.MaxLookBackDay;
        }

        rule.ModifiedAt = DateTimeOffset.UtcNow;
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
            rule.Sensors
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => new KeyValuePair<string, List<string>>(
                    item.Key,
                    item.Value.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToList())),
            StringComparer.Ordinal);
        rule.Tenants ??= [];
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
}
