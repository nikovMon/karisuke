using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Observability;
using ImagingPipeline.Rules.Api.Configuration;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Services;

/// <summary>
/// Appends audit records to the shared audit index. The change is already saved when a record is
/// written, so a failed write is logged and never fails the request, and a cancelled request still
/// gets its record.
/// </summary>
public sealed class RuleAuditWriter
{
    private readonly IElasticsearchDocumentClient _client;
    private readonly IHttpContextAccessor _httpContext;
    private readonly string _indexName;
    private readonly ILogger<RuleAuditWriter> _logger;

    public RuleAuditWriter(
        IElasticsearchDocumentClient client,
        IHttpContextAccessor httpContext,
        IOptions<RulesElasticsearchOptions> options,
        ILogger<RuleAuditWriter> logger)
    {
        _client = client;
        _httpContext = httpContext;
        _indexName = options.Value.AuditIndex;
        _logger = logger;
    }

    public async Task RecordAsync(RuleAuditRecord record)
    {
        record.RequestId ??= _httpContext.HttpContext?.TraceIdentifier;
        try
        {
            await _client.IndexAsync(
                _indexName,
                id: null,
                record,
                waitForRefresh: false,
                allowGeneratedId: true,
                CancellationToken.None);
        }
        catch (Exception exception)
        {
            using (_logger.BeginScope(new KeyValuePair<string, object?>[]
                   {
                       new(TelemetryAttributeNames.PipelineId, record.PipelineId),
                       new(TelemetryAttributeNames.PipelineRuleId, record.RuleId),
                       new(TelemetryAttributeNames.RuleChangeType, record.ChangeType.Value())
                   }))
            {
                _logger.RuleAuditWriteFailed(exception);
            }
        }
    }
}
