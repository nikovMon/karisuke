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

    public async Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        using var logScope = BeginLogScope(pipelineId, id);
        var rule = await GetByIdAsync(pipelineId, id, cancellationToken);
        if (rule is null)
        {
            _logger.PipelineRuleNotFound();
            return RuleOperationResult<PipelineRuleDocument>.NotFound($"Rule '{id}' was not found.");
        }

        var nameChanged = request.ProvidedFields.Contains("ruleName") &&
                          !string.Equals(rule.RuleName, request.RuleName, StringComparison.Ordinal);
        request.ApplyTo(rule);
        rule.UpdateTime = DateTimeOffset.UtcNow;

        // A rule that no longer passes validation, e.g. after a contract change, must still be switchable off.
        var onlyDeactivates = !rule.IsActive && request.ProvidedFields.SequenceEqual(["isActive"]);
        if (!onlyDeactivates)
        {
            var failure = await ValidateAsync(pipelineId, rule, nameChanged, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }
        }

        await Index(pipelineId).SaveAsync(id, rule, cancellationToken);
        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.RuleChangedFields, request.ProvidedFields.Order(StringComparer.Ordinal).ToArray())
               }))
        {
            _logger.PipelineRuleUpdated();
        }

        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    public Task<RuleOperationResult<PipelineRuleDocument>> ChangeActivityAsync(
        string pipelineId,
        string id,
        bool isActive,
        CancellationToken cancellationToken = default) =>
        UpdateAsync(pipelineId, id, new UpdatePipelineRuleRequest { IsActive = isActive }, cancellationToken);

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
            using (_logger.BeginScope(new KeyValuePair<string, object?>[]
                   {
                       new(TelemetryAttributeNames.RuleValidationErrors, validation.Errors.ToArray())
                   }))
            {
                _logger.PipelineRuleValidationFailed();
            }

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

    private IPipelineContract Contract(string pipelineId) =>
        _contracts.GetRequired(_catalog.GetRequired(pipelineId).ContractId);

    private static PipelineRuleDocument HydrateId(ElasticsearchDocument<PipelineRuleDocument> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }
}
