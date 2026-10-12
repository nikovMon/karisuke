using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Common.Dtos.Rules.Requests;
using ImagingPipeline.Common.Dtos.Rules.Responses;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.Rules.Api.Observability;
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
    private readonly IPipelineContractRegistry _contracts;
    private readonly ILogger<PipelineRuleService> _logger;

    public PipelineRuleService(
        IElasticsearchDocumentClient client,
        IPipelineCatalog catalog,
        IPipelineContractRegistry contracts,
        ILogger<PipelineRuleService> logger)
    {
        _client = client;
        _catalog = catalog;
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
        using var telemetry = PipelineRuleWriteTelemetry.Begin(pipelineId, ruleId: null, _logger);
        var rule = request.ToDocument();
        rule.CreationTime = rule.UpdateTime = DateTimeOffset.UtcNow;

        var failure = await ValidateAsync(pipelineId, rule, checkName: true, telemetry, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        rule.Id = await Index(pipelineId).SaveAsync<PipelineRuleDocument>(null, rule, cancellationToken);
        telemetry.Created(rule.Id);
        return RuleOperationResult<PipelineRuleDocument>.Success(rule);
    }

    public async Task<RuleOperationResult<PipelineRuleDocument>> UpdateAsync(
        string pipelineId,
        string id,
        UpdatePipelineRuleRequest request,
        CancellationToken cancellationToken = default)
    {
        using var telemetry = PipelineRuleWriteTelemetry.Begin(pipelineId, id, _logger);
        var rule = await GetByIdAsync(pipelineId, id, cancellationToken);
        if (rule is null)
        {
            telemetry.NotFound();
            return RuleOperationResult<PipelineRuleDocument>.NotFound($"Rule '{id}' was not found.");
        }

        var nameChanged = request.ProvidedFields.Contains("ruleName") &&
                          !string.Equals(rule.RuleName, request.RuleName, StringComparison.Ordinal);
        request.ApplyTo(rule);
        rule.UpdateTime = DateTimeOffset.UtcNow;

        var failure = await ValidateAsync(pipelineId, rule, nameChanged, telemetry, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        await Index(pipelineId).SaveAsync(id, rule, cancellationToken);
        telemetry.Updated(request.ProvidedFields);
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
        using var telemetry = PipelineRuleWriteTelemetry.Begin(pipelineId, id, _logger);
        if (!await Index(pipelineId).DeleteAsync<PipelineRuleDocument>(id, cancellationToken))
        {
            telemetry.NotFound();
            return RuleOperationResult.NotFound($"Rule '{id}' was not found.");
        }

        telemetry.Deleted();
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
        PipelineRuleWriteTelemetry telemetry,
        CancellationToken cancellationToken)
    {
        if (rule.Match is not null)
        {
            rule.Match.Location = null;
        }

        var validation = PipelineRuleValidator.Validate(rule, Contract(pipelineId));
        if (!validation.IsValid)
        {
            telemetry.ValidationFailed(validation.Errors);
            return RuleOperationResult<PipelineRuleDocument>.ValidationFailed(string.Join(" | ", validation.Errors));
        }

        if (validation.Geometry is not null)
        {
            rule.Match!.Location = RuleGeometry.ToGeoJson(validation.Geometry);
        }

        if (checkName &&
            await Index(pipelineId).ExistsByNameAsync<PipelineRuleDocument>(rule.RuleName, rule.Id, cancellationToken))
        {
            telemetry.NameConflict();
            return RuleOperationResult<PipelineRuleDocument>.Conflict($"Rule with ruleName '{rule.RuleName}' already exists.");
        }

        return null;
    }

    private RuleIndex Index(string pipelineId) =>
        new(_client, _catalog.GetRequired(pipelineId).RulesIndex);

    private IPipelineContract Contract(string pipelineId) =>
        _contracts.GetRequired(_catalog.GetRequired(pipelineId).ContractId);

    private static PipelineRuleDocument HydrateId(ElasticsearchDocument<PipelineRuleDocument> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }
}
