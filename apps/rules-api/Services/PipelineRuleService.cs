using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineCatalog;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Reads and writes v2 rules in each pipeline's own index, as the catalog names it. Callers
/// check that the pipeline is configured first.
/// </summary>
public sealed class PipelineRuleService
{
    private readonly IElasticsearchDocumentClient _client;
    private readonly IRuleSourceResolver _ruleSources;

    public PipelineRuleService(IElasticsearchDocumentClient client, IRuleSourceResolver ruleSources)
    {
        _client = client;
        _ruleSources = ruleSources;
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

    private RuleIndex Index(string pipelineId) =>
        new(_client, _ruleSources.Resolve(pipelineId).IndexName);

    private static PipelineRuleDocument HydrateId(ElasticsearchDocument<PipelineRuleDocument> document)
    {
        document.Source.Id = document.Id;
        return document.Source;
    }
}
