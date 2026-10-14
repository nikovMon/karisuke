using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Rules;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.RuleMigrator;

internal sealed record MigrationTarget(string IndexName, IPipelineContract Contract);

/// <summary>
/// Copies every v1 rule into a pipeline's v2 index under the same ID, so a rerun overwrites
/// instead of duplicating. A rule that fails v2 validation, or whose name a different rule in the
/// target already has, is skipped and reported; the others are still migrated.
/// </summary>
internal sealed class RuleMigration(IElasticsearchDocumentClient client)
{
    private const int PageSize = 500;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MigrationReport> RunAsync(
        MigrationTarget target,
        MigrationOptions options,
        CancellationToken cancellationToken)
    {
        var report = new MigrationReport();
        var ruleIdsByName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var existing in await ReadAllAsync<RuleNameSource>(target.IndexName, ["ruleName"], cancellationToken))
        {
            ruleIdsByName.TryAdd(existing.Source.RuleName, existing.Id);
        }

        foreach (var document in await ReadAllAsync<JsonObject>(options.SourceIndex, [], cancellationToken))
        {
            report.Read++;
            if (!TryRead(document, out var v1, out var readError))
            {
                report.Skipped.Add(new(document.Id, null, readError));
                continue;
            }

            var rule = RuleConverter.Convert(v1, options.MaxPhotoAgeDays);
            var validation = PipelineRuleValidator.Validate(rule, target.Contract);
            if (!validation.IsValid)
            {
                report.Skipped.Add(new(rule.Id, rule.RuleName, string.Join(" | ", validation.Errors)));
                continue;
            }

            if (!ruleIdsByName.TryAdd(rule.RuleName, rule.Id) && ruleIdsByName[rule.RuleName] != rule.Id)
            {
                report.Skipped.Add(new(rule.Id, rule.RuleName, $"ruleName is already used by rule '{ruleIdsByName[rule.RuleName]}'"));
                continue;
            }

            if (validation.Geometry is not null)
            {
                rule.Match!.Location = ToGeoJson(validation.Geometry);
            }

            if (options.Apply)
            {
                try
                {
                    await client.IndexAsync(target.IndexName, rule.Id, rule, waitForRefresh: false, cancellationToken: cancellationToken);
                }
                catch (ElasticsearchClientException exception)
                {
                    report.Skipped.Add(new(rule.Id, rule.RuleName, $"save failed: {exception.Message}"));
                    continue;
                }
            }

            report.Migrated.Add(rule.Id);
            if (!string.IsNullOrWhiteSpace(v1.Area))
            {
                report.DroppedAreas.Add(new(rule.Id, rule.RuleName, v1.Area));
            }
        }

        return report;
    }

    private async Task<List<ElasticsearchDocument<TDocument>>> ReadAllAsync<TDocument>(
        string indexName,
        List<string> sourceIncludes,
        CancellationToken cancellationToken)
        where TDocument : class
    {
        var documents = new List<ElasticsearchDocument<TDocument>>();
        while (true)
        {
            var page = await client.SearchDocumentsAsync<TDocument>(
                new ElasticsearchSearchRequest
                {
                    IndexName = indexName,
                    From = documents.Count,
                    Size = PageSize,
                    SourceIncludes = sourceIncludes
                },
                cancellationToken);
            documents.AddRange(page);
            if (page.Count < PageSize)
            {
                return documents;
            }
        }
    }

    private static bool TryRead(
        ElasticsearchDocument<JsonObject> document,
        [NotNullWhen(true)] out RuleDto? rule,
        out string error)
    {
        try
        {
            rule = document.Source.Deserialize<RuleDto>(JsonOptions)!;
        }
        catch (JsonException exception)
        {
            rule = null;
            error = $"not a readable v1 rule: {exception.Message}";
            return false;
        }

        rule.Id = document.Id;
        error = string.Empty;
        return true;
    }

    private static JsonElement ToGeoJson(Geometry geometry)
    {
        using var document = JsonDocument.Parse(new GeoJsonWriter().Write(geometry));
        return document.RootElement.Clone();
    }

    private sealed class RuleNameSource
    {
        [JsonPropertyName("ruleName")]
        public string RuleName { get; set; } = string.Empty;
    }
}
