using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.Observability;
using ImagingPipeline.RuleEngine.Rules;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Reads and writes v2 rules in each pipeline's own index, as the catalog names it. Writes are
/// checked with <see cref="PipelineRuleValidator"/> against the pipeline's contract, the same
/// check the gateway runs when it loads rules. Callers check that the pipeline is configured first.
/// </summary>
public sealed class PipelineRuleService
{
    private readonly IElasticsearchDocumentClient _client;
    private readonly IPipelineCatalog _catalog;
    private readonly IRuleSourceResolver _ruleSources;
    private readonly IPipelineContractRegistry _contracts;
    private readonly ILogger<PipelineRuleService> _logger;

    public PipelineRuleService(
        IElasticsearchDocumentClient client,
        IPipelineCatalog catalog,
        IRuleSourceResolver ruleSources,
        IPipelineContractRegistry contracts,
        ILogger<PipelineRuleService> logger)
    {
        _client = client;
        _catalog = catalog;
        _ruleSources = ruleSources;
        _contracts = contracts;
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

        await Index(pipelineId).SaveAsync(rule.Id, rule, cancellationToken);
        _logger.PipelineRuleCreated();
        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    public Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(pipelineId, id, request, Index(pipelineId), cancellationToken);

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
        var errors = RuleValidation.ValidateIds(ids);
        if (errors.Count > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
        }

        if (request.ProvidedFields.Contains("ruleName") && ids.Count > 1)
        {
            return RuleOperationResult<BulkOperationResult>.Conflict("Cannot set the same ruleName on multiple rules.");
        }

        var index = BulkIndex(pipelineId, ids);
        return await ForEachRuleAsync(ids, id => UpdateAsync(pipelineId, id, request, index, cancellationToken));
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
        if (!await Index(pipelineId).DeleteAsync<PipelineRuleDocument>(id, cancellationToken))
        {
            _logger.PipelineRuleNotFound();
            return RuleOperationResult.NotFound($"Rule '{id}' was not found.");
        }

        _logger.PipelineRuleDeleted();
        return RuleOperationResult.Success();
    }

    private Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        UpdatePipelineRuleRequest request,
        RuleIndex index,
        CancellationToken cancellationToken) =>
        UpdateAsync(pipelineId, id, request.ProvidedFields, rule =>
        {
            request.ApplyTo(rule);
            return null;
        }, index, cancellationToken);

    /// <summary>
    /// Loads the rule, applies <paramref name="change"/>, validates and saves it. A change can refuse
    /// a rule by returning the reason.
    /// </summary>
    private async Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        IReadOnlyCollection<string> changedFields,
        Func<PipelineRuleDocument, string?> change,
        RuleIndex index,
        CancellationToken cancellationToken)
    {
        using var logScope = BeginLogScope(pipelineId, id);
        var document = await index.GetAsync<PipelineRuleDocument>(id, cancellationToken);
        if (document is null)
        {
            _logger.PipelineRuleNotFound();
            return RuleOperationResult<PipelineRuleDocument>.NotFound($"Rule '{id}' was not found.");
        }

        var rule = HydrateId(document);
        var previousName = rule.RuleName;
        if (change(rule) is { } refusal)
        {
            LogValidationFailed([refusal]);
            return RuleOperationResult<PipelineRuleDocument>.ValidationFailed(refusal);
        }

        rule.UpdateTime = DateTimeOffset.UtcNow;

        // A rule that no longer passes validation, e.g. after a contract change, must still be switchable off.
        var onlyDeactivates = !rule.IsActive && changedFields.SequenceEqual(["isActive"]);
        if (!onlyDeactivates)
        {
            var nameChanged = !string.Equals(previousName, rule.RuleName, StringComparison.Ordinal);
            var failure = await ValidateAsync(pipelineId, rule, nameChanged, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        await index.SaveAsync(id, rule, cancellationToken);
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleChangedFields, changedFields.Order(StringComparer.Ordinal).ToArray())
               }))
        {
            _logger.PipelineRuleUpdated();
        }

        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    private async Task<RuleOperationResult<BulkOperationResult>> MutateSensorsAsync(
        string pipelineId,
        IReadOnlyCollection<string> ids,
        RuleSensorUpdateRequest request,
        bool add,
        CancellationToken cancellationToken)
    {
        var errors = RuleValidation.ValidateIds(ids).Concat(RuleValidation.ValidateSensorRequest(request)).ToArray();
        if (errors.Length > 0)
        {
            return RuleOperationResult<BulkOperationResult>.ValidationFailed(string.Join(" | ", errors));
        }

        var index = BulkIndex(pipelineId, ids);
        return await ForEachRuleAsync(ids, id => UpdateAsync(
            pipelineId,
            id,
            ["match.sensors"],
            rule => add ? AddSensor(rule, request) : RemoveSensor(rule, request),
            index,
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

    private static async Task<RuleOperationResult<BulkOperationResult>> ForEachRuleAsync(
        IReadOnlyCollection<string> ids,
        Func<string, Task<RuleOperationResult<PipelineRuleDocument>>> update)
    {
        var result = new BulkOperationResult();
        foreach (var id in ids)
        {
            var updated = await update(id);
            if (updated.Status == RuleOperationStatus.Success)
            {
                result.SuccessIds.Add(id);
            }
            else
            {
                result.FailedIds.Add(new BulkOperationFailure(id, updated.Error ?? "Rule update failed"));
            }
        }

        return result.ToOperationResult();
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
        new(_client, _ruleSources.Resolve(pipelineId).IndexName, waitForRefresh: true);

    // Waiting for a refresh per rule would make a large bulk request slow. Only a single-rule
    // request can rename a rule, which is what needs the refresh.
    private RuleIndex BulkIndex(string pipelineId, IReadOnlyCollection<string> ids) =>
        new(_client, _ruleSources.Resolve(pipelineId).IndexName, waitForRefresh: ids.Count == 1);

    private IPipelineContract Contract(string pipelineId) =>
        _contracts.GetRequired(_catalog.GetRequired(pipelineId).ContractId);

    private static PipelineRuleDocument HydrateId(ElasticsearchDocument<PipelineRuleDocument> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }
}
