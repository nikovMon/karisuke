using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api;

internal static partial class RulesApiLog
{
    // Request-level failures. Method, route and details travel in the log scope.
    [LoggerMessage(1001, LogLevel.Warning, "Request model validation failed.")]
    public static partial void RequestModelValidationFailed(this ILogger logger);

    [LoggerMessage(1002, LogLevel.Error, "Elasticsearch persistence operation failed.")]
    public static partial void PersistenceOperationFailed(this ILogger logger, Exception exception);

    [LoggerMessage(1003, LogLevel.Error, "Unexpected server error.")]
    public static partial void UnexpectedServerError(this ILogger logger, Exception? exception);

    // The pipeline ID travels as a structured field in the log scope, not in the message.
    [LoggerMessage(5030, LogLevel.Warning, "Requested pipeline is not configured.")]
    public static partial void PipelineNotFound(this ILogger logger);

    // Pipeline rule writes. Pipeline ID, rule ID and details travel in the log scope.
    [LoggerMessage(5031, LogLevel.Information, "Pipeline rule created.")]
    public static partial void PipelineRuleCreated(this ILogger logger);

    [LoggerMessage(5032, LogLevel.Information, "Pipeline rule updated.")]
    public static partial void PipelineRuleUpdated(this ILogger logger);

    [LoggerMessage(5033, LogLevel.Information, "Pipeline rule deleted.")]
    public static partial void PipelineRuleDeleted(this ILogger logger);

    [LoggerMessage(5034, LogLevel.Warning, "Pipeline rule failed validation.")]
    public static partial void PipelineRuleValidationFailed(this ILogger logger);

    [LoggerMessage(5035, LogLevel.Warning, "Pipeline rule was not found.")]
    public static partial void PipelineRuleNotFound(this ILogger logger);

    [LoggerMessage(5036, LogLevel.Warning, "Pipeline rule name is already in use.")]
    public static partial void PipelineRuleNameConflict(this ILogger logger);

    [LoggerMessage(5037, LogLevel.Warning, "Pipeline rule could not be saved during a bulk request.")]
    public static partial void PipelineRuleBulkItemFailed(this ILogger logger, Exception exception);

    [LoggerMessage(5038, LogLevel.Information, "Pipeline rule bulk request completed.")]
    public static partial void PipelineRuleBulkCompleted(this ILogger logger);

    [LoggerMessage(5039, LogLevel.Warning, "Rule audit record could not be written; the rule change itself was saved.")]
    public static partial void RuleAuditWriteFailed(this ILogger logger, Exception exception);

    // The status code and failure type travel in the log scope.
    [LoggerMessage(5050, LogLevel.Warning, "Elasticsearch health probe returned an invalid response.")]
    public static partial void ElasticsearchHealthInvalidResponse(this ILogger logger);

    [LoggerMessage(5051, LogLevel.Warning, "Elasticsearch health probe failed.")]
    public static partial void ElasticsearchHealthProbeFailed(
        this ILogger logger,
        Exception exception);
}
