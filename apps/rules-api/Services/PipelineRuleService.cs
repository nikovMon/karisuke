using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.Observability;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.Rules.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Reads and writes v2 rules in each pipeline's own index, as the catalog names it. Writes are
/// checked with <see cref="PipelineRuleValidator"/> against the pipeline's contract, the same
/// check the gateway runs when it loads rules. Callers check that the pipeline is configured first.
/// </summary>
public sealed class PipelineRuleService
{
    private const int MaxLoggedFailedIds = 10;

    private readonly IElasticsearchDocumentClient _client;
    private readonly IPipelineCatalog _catalog;
    private readonly IRuleSourceResolver _ruleSources;
    private readonly IPipelineContractRegistry _contracts;
    private readonly RuleAuditWriter _audit;
    private readonly RulesElasticsearchOptions _options;
    private readonly ILogger<PipelineRuleService> _logger;

    public PipelineRuleService(
        IElasticsearchDocumentClient client,
        IPipelineCatalog catalog,
        IRuleSourceResolver ruleSources,
        IPipelineContractRegistry contracts,
        RuleAuditWriter audit,
        IOptions<RulesElasticsearchOptions> options,
        ILogger<PipelineRuleService> logger)
    {
        _client = client;
        _catalog = catalog;
        _ruleSources = ruleSources;
        _contracts = contracts;
        _audit = audit;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyList<PipelineRuleDocument>> GetRulesAsync(
        string pipelineId,
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default)
    {
        var documents = await Index(pipelineId).SearchAsync<PipelineRuleDocument>(isActive, from, size, cancellationToken);
        return documents
            .Select(HydrateId)
            .ToArray();
    }

    public Task<IReadOnlyList<string>> GetRuleNamesAsync(
        string pipelineId,
        bool? isActive,
        int from,
        int size,
        CancellationToken cancellationToken = default) =>
        Index(pipelineId).SearchNamesAsync(isActive, from, size, cancellationToken);

    public async Task<PipelineRuleDocument?> GetByIdAsync(
        string pipelineId,
        string id,
        CancellationToken cancellationToken = default)
    {
        var document = await Index(pipelineId).GetAsync<PipelineRuleDocument>(id, cancellationToken);
        return document is null ? null : HydrateId(document);
    }

    public async Task<PipelineRuleDocument?> GetByNameAsync(
        string pipelineId,
        string ruleName,
        CancellationToken cancellationToken = default)
    {
        var document = await Index(pipelineId).GetByNameAsync<PipelineRuleDocument>(ruleName, cancellationToken);
        return document is null ? null : HydrateId(document);
    }

    public async Task<RuleOperationResult<PipelineRuleDocument>> CreateAsync(
        string pipelineId,
        CreatePipelineRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var rule = request.ToDocument();
        rule.Id = Guid.NewGuid().ToString("N");
        rule.CreationTime = rule.UpdateTime = DateTimeOffset.UtcNow;
        using var logScope = BeginLogScope(pipelineId, rule.Id);

        var failure = await ValidateAsync(pipelineId, rule, checkName: true, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        await Index(pipelineId).SaveAsync(rule.Id, rule, waitForRefresh: true, cancellationToken);
        _logger.PipelineRuleCreated();
        await _audit.RecordAsync(new RuleAuditRecord
        {
            PipelineId = pipelineId,
            RuleId = rule.Id,
            ChangeType = RuleChangeType.Created,
            NewState = Snapshot(rule),
            ChangedAt = rule.CreationTime
        });
        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    public Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateRuleAsync(pipelineId, id, Apply(request), inBulk: false, cancellationToken);

    public Task<RuleOperationResult<PipelineRuleDocument>> ChangeActivityAsync(
        string pipelineId,
        string id,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(pipelineId, id, new UpdatePipelineRuleRequest { IsActive = isActive }, cancellationToken);

    /// <summary>
    /// Applies one update to each rule; each is validated and saved on its own. Setting one name on
    /// several rules is rejected up front.
    /// </summary>
    public async Task<RuleOperationResult<BulkOperationResult>> UpdateBulkAsync(
        string pipelineId,
        IReadOnlyCollection<string> ids,
        UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        var uniqueIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        var errors = ValidateBulkIds(uniqueIds);
        if (errors.Count > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
        }

        if (request.ProvidedFields.Contains(PipelineRuleFields.RuleName) && uniqueIds.Length > 1)
        {
            return RuleOperationResult<BulkOperationResult>.Conflict("Cannot set the same ruleName on multiple rules.");
        }

        return await ForEachRuleAsync(pipelineId, "bulk_update", uniqueIds, id => UpdateRuleAsync(
            pipelineId, id, Apply(request), inBulk: true, cancellationToken));
    }

    public Task<RuleOperationResult<BulkOperationResult>> AddSensorsAsync(
        string pipelineId,
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        MutateSensorsAsync(pipelineId, ids, request, add: true, cancellationToken);

    public Task<RuleOperationResult<BulkOperationResult>> RemoveSensorsAsync(
        string pipelineId,
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        CancellationToken cancellationToken = default) =>
        MutateSensorsAsync(pipelineId, ids, request, add: false, cancellationToken);

    public async Task<RuleOperationResult> DeleteAsync(
        string pipelineId,
        string id,
        CancellationToken cancellationToken = default)
    {
        using var logScope = BeginLogScope(pipelineId, id);
        var index = Index(pipelineId);
        var document = await index.GetAsync<PipelineRuleDocument>(id, cancellationToken);
        if (document is null || !await index.DeleteAsync<PipelineRuleDocument>(id, waitForRefresh: true, cancellationToken))
        {
            _logger.PipelineRuleNotFound();
            return RuleOperationResult.NotFound($"Rule '{id}' was not found.");
        }

        _logger.PipelineRuleDeleted();
        await _audit.RecordAsync(new RuleAuditRecord
        {
            PipelineId = pipelineId,
            RuleId = id,
            ChangeType = RuleChangeType.Deleted,
            PreviousState = Snapshot(HydrateId(document)),
            ChangedAt = DateTimeOffset.UtcNow
        });
        return RuleOperationResult.Success();
    }

    /// <summary>
    /// Loads the rule, applies <paramref name="change"/>, validates and saves it. A change can refuse
    /// a rule by returning the reason; a change that leaves the rule as it was saves nothing. What
    /// changed is taken from the stored rule before and after, not from what the request sent.
    /// </summary>
    private async Task<RuleOperationResult<PipelineRuleDocument>> UpdateRuleAsync(
        string pipelineId,
        string id,
        Func<PipelineRuleDocument, string?> change,
        bool inBulk,
        CancellationToken cancellationToken)
    {
        using var logScope = BeginLogScope(pipelineId, id);
        var index = Index(pipelineId);
        var document = await index.GetAsync<PipelineRuleDocument>(id, cancellationToken);
        if (document is null)
        {
            if (!inBulk)
            {
                _logger.PipelineRuleNotFound();
            }

            return RuleOperationResult<PipelineRuleDocument>.NotFound($"Rule '{id}' was not found.");
        }

        var rule = HydrateId(document);
        var before = Snapshot(rule);
        if (change(rule) is { } refusal)
        {
            LogValidationFailed([refusal]);
            return RuleOperationResult<PipelineRuleDocument>.ValidationFailed(refusal);
        }

        var changedFields = ChangedFields(before, Snapshot(rule));
        if (changedFields.Count == 0)
        {
            return RuleOperationResult<PipelineRuleDocument>.Success(rule);
        }

        rule.UpdateTime = DateTimeOffset.UtcNow;
        var nameChanged = !string.Equals(before.GetProperty(PipelineRuleFields.RuleName).GetString(), rule.RuleName, StringComparison.Ordinal);

        // A rule that no longer passes validation, e.g. after a contract change, must still be switchable off.
        var onlyDeactivates = !rule.IsActive && changedFields.SequenceEqual([PipelineRuleFields.IsActive]);
        if (!onlyDeactivates)
        {
            var failure = await ValidateAsync(pipelineId, rule, nameChanged, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        await index.SaveAsync(id, rule, waitForRefresh: nameChanged, cancellationToken);
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleChangedFields, changedFields)
               }))
        {
            _logger.PipelineRuleUpdated();
        }

        await _audit.RecordAsync(new RuleAuditRecord
        {
            PipelineId = pipelineId,
            RuleId = id,
            ChangeType = ChangeType(changedFields, rule),
            PreviousState = before,
            NewState = Snapshot(rule),
            ChangedFields = changedFields,
            ChangedAt = rule.UpdateTime
        });
        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    private static RuleChangeType ChangeType(IReadOnlyList<string> changedFields, PipelineRuleDocument rule) =>
        !changedFields.SequenceEqual(["isActive"]) ? RuleChangeType.Updated
        : rule.IsActive ? RuleChangeType.Activated
        : RuleChangeType.Deactivated;

    /// <summary>
    /// The fields that differ between two snapshots, as top-level names or <c>match.*</c> paths.
    /// Timestamps and the derived <c>match.location</c> are maintained by the server and left out.
    /// </summary>
    private static IReadOnlyList<string> ChangedFields(JsonElement before, JsonElement after)
    {
        var changed = new List<string>();
        foreach (var name in PropertyNames(before).Union(PropertyNames(after)))
        {
            if (name is "creationTime" or "updateTime")
            {
                continue;
            }

            if (name == "match")
            {
                var beforeMatch = Property(before, name);
                var afterMatch = Property(after, name);
                changed.AddRange(PropertyNames(beforeMatch).Union(PropertyNames(afterMatch))
                    .Where(field => field != "location" && !Same(Property(beforeMatch, field), Property(afterMatch, field)))
                    .Select(field => $"match.{field}"));
            }
            else if (!Same(Property(before, name), Property(after, name)))
            {
                changed.Add(name);
            }
        }

        return changed.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<string> PropertyNames(JsonElement? element) =>
        element is { ValueKind: JsonValueKind.Object } value ? value.EnumerateObject().Select(property => property.Name) : [];

    private static JsonElement? Property(JsonElement? element, string name) =>
        element is { ValueKind: JsonValueKind.Object } value && value.TryGetProperty(name, out var property) ? property : null;

    private static bool Same(JsonElement? before, JsonElement? after) =>
        before is null ? after is null : after is not null && JsonElement.DeepEquals(before.Value, after.Value);

    /// <summary>The rule as stored, without its ID, which the audit record carries on its own.</summary>
    private static JsonElement Snapshot(PipelineRuleDocument rule)
    {
        var snapshot = JsonSerializer.SerializeToNode(rule)!.AsObject();
        snapshot.Remove("_id");
        return JsonSerializer.SerializeToElement(snapshot);
    }

    private static Func<PipelineRuleDocument, string?> Apply(UpdatePipelineRuleRequest request) => rule =>
    {
        request.ApplyTo(rule);
        return null;
    };

    private async Task<RuleOperationResult<BulkOperationResult>> MutateSensorsAsync(
        string pipelineId,
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        bool add,
        CancellationToken cancellationToken)
    {
        var uniqueIds = ids.Distinct(StringComparer.Ordinal).ToArray();
        var errors = ValidateBulkIds(uniqueIds).Concat(RuleValidation.ValidateSensorRequest(request)).ToArray();
        if (errors.Length > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
        }

        return await ForEachRuleAsync(pipelineId, add ? "add_sensors" : "remove_sensors", uniqueIds, id => UpdateRuleAsync(
            pipelineId,
            id,
            rule => add ? AddSensor(rule, request) : RemoveSensor(rule, request),
            inBulk: true,
            cancellationToken));
    }

    private static string? AddSensor(PipelineRuleDocument rule, RuleSensorUpdateRequest request)
    {
        rule.Match ??= new RuleMatchConditions();
        rule.Match.Sensors ??= [];
        RuleSensors.Add(rule.Match.Sensors, request);
        return null;
    }

    private static string? RemoveSensor(PipelineRuleDocument rule, RuleSensorUpdateRequest request)
    {
        if (rule.Match?.Sensors is not { } sensors)
        {
            return null;
        }

        RuleSensors.Remove(sensors, request);
        return sensors.Count == 0
            ? "Removing the rule's last sensor would let it match every sensor; send match.sensors as null in a PATCH to do that on purpose."
            : null;
    }

    private IReadOnlyList<string> ValidateBulkIds(IReadOnlyCollection<string> ids)
    {
        var errors = RuleValidation.ValidateIds(ids);
        return errors.Count == 0 && ids.Count > _options.MaxBulkSize
            ? [$"ids list cannot contain more than {_options.MaxBulkSize} ids."]
            : errors;
    }

    /// <summary>
    /// Runs <paramref name="update"/> for each rule. A rule that cannot be saved is reported as
    /// failed rather than failing the request, so the caller learns exactly which rules changed.
    /// </summary>
    private async Task<RuleOperationResult<BulkOperationResult>> ForEachRuleAsync(
        string pipelineId,
        string operation,
        IReadOnlyCollection<string> ids,
        Func<string, Task<RuleOperationResult<PipelineRuleDocument>>> update)
    {
        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            try
            {
                var updated = await update(id);
                if (updated.Status == RuleOperationStatus.Success)
                {
                    result.SuccessIds.Add(id);
                }
                else
                {
                    result.FailedIds.Add(new BulkOperationFailure(id, updated.Error ?? "Rule update failed."));
                }
            }
            catch (RulePersistenceException exception)
            {
                using (BeginLogScope(pipelineId, id))
                {
                    _logger.PipelineRuleBulkItemFailed(exception);
                }

                result.FailedIds.Add(new BulkOperationFailure(id, "The rule could not be saved."));
            }
        }

        LogBulkCompleted(pipelineId, operation, ids.Count, result);
        return result.ToOperationResult();
    }

    private void LogBulkCompleted(string pipelineId, string operation, int requestedCount, BulkOperationResult result)
    {
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.PipelineId, pipelineId),
                   new(TelemetryAttributeNames.RulesOperation, operation),
                   new(TelemetryAttributeNames.RulesRequestedCount, requestedCount),
                   new(TelemetryAttributeNames.RulesSucceededCount, result.SuccessIds.Count),
                   new(TelemetryAttributeNames.RulesFailedCount, result.FailedIds.Count),
                   new(TelemetryAttributeNames.RulesFailedIds, result.FailedIds.Take(MaxLoggedFailedIds).Select(failure => failure.Id).ToArray())
               }))
        {
            _logger.PipelineRuleBulkCompleted();
        }
    }

    private void LogValidationFailed(IReadOnlyList<string> errors)
    {
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleValidationErrors, errors.ToArray())
               }))
        {
            _logger.PipelineRuleValidationFailed();
        }
    }

    /// <summary>
    /// Validates the rule against its pipeline's contract, derives <c>match.location</c> from the
    /// WKT, and checks the name is free in the pipeline. Returns null when the rule can be saved.
    /// </summary>
    private async Task<RuleOperationResult<PipelineRuleDocument>?> ValidateAsync(
        string pipelineId,
        PipelineRuleDocument rule,
        bool checkName,
        CancellationToken cancellationToken)
    {
        if (rule.Match is not null)
        {
            rule.Match.Location = null;
        }

        var validation = PipelineRuleValidator.Validate(rule, Contract(pipelineId));
        if (!validation.IsValid)
        {
            LogValidationFailed(validation.Errors);
            return RuleOperationResult<PipelineRuleDocument>.ValidationFailed(string.Join(" | ", validation.Errors));
        }

        if (validation.Geometry is not null)
        {
            rule.Match!.Location = RuleGeometry.ToGeoJson(validation.Geometry);
        }

        if (checkName &&
            await Index(pipelineId).ExistsByNameAsync<PipelineRuleDocument>(rule.RuleName, rule.Id, cancellationToken))
        {
            _logger.PipelineRuleNameConflict();
            return RuleOperationResult<PipelineRuleDocument>.Conflict($"Rule with ruleName '{rule.RuleName}' already exists.");
        }

        return null;
    }

    private IDisposable? BeginLogScope(string pipelineId, string ruleId) =>
        _logger.BeginScope(new KeyValuePair<string, object?>[]
        {
            new(TelemetryAttributeNames.PipelineId, pipelineId),
            new(TelemetryAttributeNames.PipelineRuleId, ruleId)
        });

    private RuleIndex Index(string pipelineId) =>
        new(_client, _ruleSources.Resolve(pipelineId).IndexName);

    private IPipelineContract Contract(string pipelineId) =>
        _contracts.GetRequired(_catalog.GetRequired(pipelineId).ContractId);

    private static PipelineRuleDocument HydrateId(ElasticsearchDocument<PipelineRuleDocument> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }
}
