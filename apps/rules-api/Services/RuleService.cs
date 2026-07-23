using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Rules.Api.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ImagingPipeline.Rules.Api.Services;

public sealed class RuleService : IRuleService
{
    private const string RuleNameKeywordField = "ruleName.keyword";
    private const string IsActiveField = "isActive";
    private const string RuleNameField = "ruleName";

    private readonly IElasticsearchDocumentClient _client;
    private readonly string _indexName;
    private readonly ILogger<RuleService> _logger;

    public RuleService(
        IElasticsearchDocumentClient client,
        IOptions<RulesElasticsearchOptions> options,
        ILogger<RuleService> logger)
    {
        _client = client;
        _indexName = options.Value.IndexName;
        _logger = logger;
    }

    public async Task<IReadOnlyList<RuleDto>> GetRulesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var documents = await SearchRulesAsync(
            BuildRulesSearchRequest(isActive, from, size),
            cancellationToken);

        return documents
            .Select(HydrateId)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetRuleNamesAsync(
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var request = BuildRulesSearchRequest(isActive, from, size);
        request.SourceIncludes.Add(RuleNameField);

        var documents = await SearchRulesAsync<RuleNameProjection>(request, cancellationToken);
        return documents
            .Select(document => document.Source.RuleName)
            .ToArray();
    }

    public async Task<RuleDto?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        var document = await ExecuteElasticAsync(
            () => _client.GetDocumentAsync<RuleDto>(_indexName, id, cancellationToken),
            $"get rule '{id}'");

        return document is null ? null : HydrateId(document);
    }

    public async Task<RuleDto?> GetByNameAsync(string ruleName, CancellationToken cancellationToken = default)
    {
        var documents = await SearchRulesAsync(
            new ElasticsearchSearchRequest
            {
                IndexName = _indexName,
                Size = 1,
                TermFilters =
                [
                    new ElasticsearchTermFilter
                    {
                        Field = RuleNameKeywordField,
                        Value = ruleName
                    }
                ]
            },
            cancellationToken);

        var document = documents.FirstOrDefault();
        return document is null ? null : HydrateId(document);
    }

    public async Task<RuleOperationResult<RuleDto>> CreateAsync(
        CreateRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var rule = request.ToRuleDto();
        rule.Id = string.Empty;
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}; RuleName: {RuleName}",
            "create",
            rule.Id,
            rule.RuleName);

        var errors = RuleValidation.ValidateRule(rule).ToList();
        if (!string.IsNullOrWhiteSpace(rule.LocationWkt))
        {
            if (RuleGeometry.ConvertWktToGeoJson(rule.LocationWkt, out var geoJson, out var geometryError))
            {
                rule.LocationGeoJson = geoJson;
            }
            else
            {
                errors.Add(geometryError!);
            }
        }

        if (errors.Count > 0)
        {
            LogValidationFailure("create", rule.Id, errors);
            return RuleOperationResult<RuleDto>.ValidationFailed(string.Join(" | ", errors));
        }

        if (await ExistsByNameAsync(rule.RuleName, cancellationToken: cancellationToken))
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because ruleName {RuleName} already exists. RuleId: {RuleId}",
                "create",
                rule.RuleName,
                rule.Id);
            return RuleOperationResult<RuleDto>.Conflict($"Rule with ruleName '{rule.RuleName}' already exists.");
        }

        var now = DateTimeOffset.UtcNow;
        rule.CreationTime = now;
        rule.UpdateTime = now;
        NormalizeRuleCollections(rule);

        await SaveAsync(rule, cancellationToken);
        _logger.LogInformation(
            "Rule operation {Operation} succeeded. RuleId: {RuleId}; RuleName: {RuleName}",
            "create",
            rule.Id,
            rule.RuleName);
        return RuleOperationResult<RuleDto>.Success(rule);
    }

    public Task<RuleOperationResult<RuleDto>> UpdateAsync(
        string id,
        UpdateRuleRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateExistingRuleAsync(id, request, "update", cancellationToken);

    private async Task<RuleOperationResult<RuleDto>> UpdateExistingRuleAsync(
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
            return RuleOperationResult<RuleDto>.ValidationFailed(string.Join(" | ", validationErrors));
        }

        JsonElement? locationGeoJson = null;
        if (request.HasField("locationWkt"))
        {
            if (!RuleGeometry.ConvertWktToGeoJson(request.LocationWkt, out var convertedGeoJson, out var geometryError))
            {
                LogValidationFailure(operation, id, [geometryError!]);
                return RuleOperationResult<RuleDto>.ValidationFailed(geometryError!);
            }

            locationGeoJson = convertedGeoJson;
        }

        var rule = await GetByIdAsync(id, cancellationToken);
        if (rule is null)
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because the rule was not found. RuleId: {RuleId}",
                operation,
                id);
            return RuleOperationResult<RuleDto>.NotFound($"Rule '{id}' was not found.");
        }

        if (request.HasField("ruleName") &&
            !string.Equals(rule.RuleName, request.RuleName, StringComparison.Ordinal) &&
            await ExistsByNameAsync(request.RuleName!, id, cancellationToken))
        {
            _logger.LogWarning(
                "Rule operation {Operation} failed because ruleName {RuleName} already exists. RuleId: {RuleId}",
                operation,
                request.RuleName,
                id);
            return RuleOperationResult<RuleDto>.Conflict($"Rule with ruleName '{request.RuleName}' already exists.");
        }

        ApplyUpdate(rule, request);
        if (locationGeoJson.HasValue)
        {
            rule.LocationGeoJson = locationGeoJson.Value;
        }
        var ruleErrors = RuleValidation.ValidateRule(rule);
        if (ruleErrors.Count > 0)
        {
            LogValidationFailure(operation, id, ruleErrors);
            return RuleOperationResult<RuleDto>.ValidationFailed(string.Join(" | ", ruleErrors));
        }

        await SaveAsync(rule, cancellationToken);
        _logger.LogInformation(
            "Rule operation {Operation} succeeded. RuleId: {RuleId}; UpdatedFieldCount: {UpdatedFieldCount}",
            operation,
            id,
            request.ProvidedFields.Count);
        return RuleOperationResult<RuleDto>.Success(rule);
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
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
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
            var updated = await UpdateExistingRuleAsync(id, request, "bulk_update_item", cancellationToken);
            AddBulkResult(result, id, updated.Status, updated.Error);
        }

        LogBulkOutcome("bulk_update", ids.Count, result);
        return BuildBulkOperationResult(result);
    }

    public async Task<RuleOperationResult> DeleteAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}",
            "delete",
            id);

        var deleted = await DeleteRuleAsync(id, cancellationToken);
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

    public async Task<RuleOperationResult<RuleDto>> ChangeActivityAsync(
        string id,
        ChangeRuleActivationStatusRequest request,
        CancellationToken cancellationToken = default)
    {
        _logger.LogDebug(
            "Starting rule operation {Operation}. RuleId: {RuleId}; IsActive: {IsActive}",
            "change_activity",
            id,
            request.IsActive);

        var update = new UpdateRuleRequest
        {
            IsActive = request.IsActive!.Value
        };
        update.ProvidedFields.Add("isActive");
        return await UpdateExistingRuleAsync(id, update, "change_activity", cancellationToken);
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
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
        }

        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var rule = await GetByIdAsync(id, cancellationToken);
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
            NormalizeRuleCollections(rule);
            await SaveAsync(rule, cancellationToken);
            result.SuccessIds.Add(id);
        }

        LogBulkOutcome(operation, ids.Count, result, request.SensorName);
        return BuildBulkOperationResult(result);
    }

    private async Task<bool> ExistsByNameAsync(
        string ruleName,
        string? excludingId = null,
        CancellationToken cancellationToken = default)
    {
        var request = new ElasticsearchSearchRequest
        {
            IndexName = _indexName,
            Size = 1,
            TermFilters =
            [
                new ElasticsearchTermFilter
                {
                    Field = RuleNameKeywordField,
                    Value = ruleName
                }
            ]
        };

        if (!string.IsNullOrWhiteSpace(excludingId))
        {
            request.ExcludedIds.Add(excludingId);
        }

        var documents = await SearchRulesAsync(request, cancellationToken);
        return documents.Count > 0;
    }

    private async Task SaveAsync(RuleDto rule, CancellationToken cancellationToken)
    {
        rule.Id = await ExecuteElasticAsync(
            () => _client.IndexAsync(
                _indexName,
                rule.Id,
                rule,
                waitForRefresh: false,
                allowGeneratedId: true,
                cancellationToken),
            $"save rule '{rule.Id}'");
    }

    private Task<bool> DeleteRuleAsync(string id, CancellationToken cancellationToken) =>
        ExecuteElasticAsync(
            () => _client.DeleteAsync<RuleDto>(
                _indexName,
                id,
                waitForRefresh: false,
                cancellationToken),
            $"delete rule '{id}'");

    private Task<IReadOnlyList<ElasticsearchDocument<RuleDto>>> SearchRulesAsync(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken) =>
        SearchRulesAsync<RuleDto>(request, cancellationToken);

    private Task<IReadOnlyList<ElasticsearchDocument<TDocument>>> SearchRulesAsync<TDocument>(
        ElasticsearchSearchRequest request,
        CancellationToken cancellationToken)
        where TDocument : class =>
        ExecuteElasticAsync(
            () => _client.SearchDocumentsAsync<TDocument>(request, cancellationToken),
            "search rules");

    private ElasticsearchSearchRequest BuildRulesSearchRequest(bool? isActive, int from, int size)
    {
        var request = new ElasticsearchSearchRequest
        {
            IndexName = _indexName,
            From = from,
            Size = size
        };

        if (isActive.HasValue)
        {
            request.TermFilters.Add(new ElasticsearchTermFilter
            {
                Field = IsActiveField,
                Value = isActive.Value
            });
        }

        return request;
    }

    private static async Task<TResult> ExecuteElasticAsync<TResult>(
        Func<Task<TResult>> operation,
        string description)
    {
        try
        {
            return await operation();
        }
        catch (ElasticsearchClientException exception)
        {
            throw new RulePersistenceException($"Elasticsearch failed to {description}: {exception.Message}");
        }
    }

    private static RuleDto HydrateId(ElasticsearchDocument<RuleDto> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }

    private static RuleOperationResult<BulkOperationResult> BuildBulkOperationResult(
        BulkOperationResult result)
    {
        if (result.FailedIds.Count == 0)
        {
            return RuleOperationResult<BulkOperationResult>.Success(result);
        }

        return result.SuccessIds.Count == 0
            ? RuleOperationResult<BulkOperationResult>.AllFailed(result)
            : RuleOperationResult<BulkOperationResult>.PartialSuccess(result);
    }

    private static void ApplyUpdate(RuleDto rule, UpdateRuleRequest request)
    {
        var updates = new Dictionary<string, Action>(StringComparer.Ordinal)
        {
            ["ruleName"] = () => rule.RuleName = request.RuleName ?? string.Empty,
            ["description"] = () => rule.Description = request.Description,
            ["algorithmName"] = () => rule.AlgorithmName = request.AlgorithmName!.Value,
            ["sensors"] = () => rule.Sensors = request.Sensors ?? new Dictionary<string, List<RegistrationQuality>>(StringComparer.Ordinal),
            ["isActive"] = () => rule.IsActive = request.IsActive.GetValueOrDefault(),
            ["tenantsInfo"] = () => rule.TenantsInfo = request.TenantsInfo ?? [],
            ["minimumResolution"] = () => rule.MinimumResolution = request.MinimumResolution.GetValueOrDefault(),
            ["maximumResolution"] = () => rule.MaximumResolution = request.MaximumResolution!.Value,
            ["area"] = () => rule.Area = request.Area ?? string.Empty,
            ["locationWkt"] = () => rule.LocationWkt = request.LocationWkt!,
            ["isPhotoOld"] = () => rule.IsPhotoOld = request.IsPhotoOld
        };

        foreach (var field in request.ProvidedFields)
        {
            if (updates.TryGetValue(field, out var update))
            {
                update();
            }
        }

        rule.UpdateTime = DateTimeOffset.UtcNow;
        NormalizeRuleCollections(rule);
    }

    private static void AddSensorValues(RuleDto rule, RuleSensorUpdateRequest request)
    {
        if (!rule.Sensors.TryGetValue(request.SensorName, out var values))
        {
            rule.Sensors[request.SensorName] = request.Values.Distinct().ToList();
            return;
        }

        foreach (var value in request.Values)
        {
            if (!values.Contains(value))
            {
                values.Add(value);
            }
        }
    }

    private static void RemoveSensorValues(RuleDto rule, RuleSensorUpdateRequest request)
    {
        if (!rule.Sensors.TryGetValue(request.SensorName, out var values))
        {
            return;
        }

        values.RemoveAll(value => request.Values.Contains(value));
        if (values.Count == 0)
        {
            rule.Sensors.Remove(request.SensorName);
        }
    }

    private static void NormalizeRuleCollections(RuleDto rule)
    {
        rule.Sensors = new Dictionary<string, List<RegistrationQuality>>(
            (rule.Sensors ?? new Dictionary<string, List<RegistrationQuality>>(StringComparer.Ordinal))
                .Where(item => !string.IsNullOrWhiteSpace(item.Key))
                .Select(item => new KeyValuePair<string, List<RegistrationQuality>>(
                    item.Key,
                    (item.Value ?? [])
                        .Distinct()
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

    private sealed class RuleNameProjection
    {
        [JsonPropertyName("ruleName")]
        public string RuleName { get; set; } = string.Empty;
    }
}
