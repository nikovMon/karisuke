using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api;

internal static partial class RulesApiLog
{
    [LoggerMessage(5000, LogLevel.Debug, "Starting rule operation {Operation}. RuleId: {RuleId}; RuleName: {RuleName}")]
    public static partial void RuleOperationStartingWithName(
        this ILogger logger,
        string operation,
        string ruleId,
        string ruleName);

    [LoggerMessage(5001, LogLevel.Debug, "Starting rule operation {Operation}. RuleId: {RuleId}; UpdatedFields: {UpdatedFields}")]
    public static partial void RuleOperationStartingWithFields(
        this ILogger logger,
        string operation,
        string ruleId,
        string updatedFields);

    [LoggerMessage(5002, LogLevel.Debug, "Starting rule operation {Operation}. RequestedCount: {RequestedCount}; UpdatedFields: {UpdatedFields}")]
    public static partial void BulkRuleOperationStarting(
        this ILogger logger,
        string operation,
        int requestedCount,
        string updatedFields);

    [LoggerMessage(5003, LogLevel.Debug, "Starting rule operation {Operation}. RuleId: {RuleId}")]
    public static partial void RuleOperationStarting(
        this ILogger logger,
        string operation,
        string ruleId);

    [LoggerMessage(5004, LogLevel.Debug, "Starting rule operation {Operation}. RuleId: {RuleId}; IsActive: {IsActive}")]
    public static partial void RuleActivityOperationStarting(
        this ILogger logger,
        string operation,
        string ruleId,
        bool? isActive);

    [LoggerMessage(5005, LogLevel.Debug, "Starting rule operation {Operation}. RequestedCount: {RequestedCount}; SensorName: {SensorName}; SensorValueCount: {SensorValueCount}")]
    public static partial void RuleSensorOperationStarting(
        this ILogger logger,
        string operation,
        int requestedCount,
        string sensorName,
        int sensorValueCount);

    [LoggerMessage(5010, LogLevel.Warning, "Rule operation {Operation} failed because ruleName {RuleName} already exists. RuleId: {RuleId}")]
    public static partial void RuleNameConflict(
        this ILogger logger,
        string operation,
        string ruleName,
        string ruleId);

    [LoggerMessage(5011, LogLevel.Warning, "Rule operation {Operation} failed because the rule was not found. RuleId: {RuleId}")]
    public static partial void RuleNotFound(
        this ILogger logger,
        string operation,
        string ruleId);

    [LoggerMessage(5012, LogLevel.Warning, "Rule operation {Operation} failed because one ruleName cannot be assigned to multiple rules. RuleCount: {RuleCount}")]
    public static partial void BulkRuleNameConflict(
        this ILogger logger,
        string operation,
        int ruleCount);

    [LoggerMessage(5020, LogLevel.Debug, "Rule operation {Operation} succeeded. RuleId: {RuleId}; RuleName: {RuleName}")]
    public static partial void RuleCreated(
        this ILogger logger,
        string operation,
        string ruleId,
        string ruleName);

    [LoggerMessage(5021, LogLevel.Debug, "Rule operation {Operation} succeeded. RuleId: {RuleId}; UpdatedFieldCount: {UpdatedFieldCount}")]
    public static partial void RuleUpdated(
        this ILogger logger,
        string operation,
        string ruleId,
        int updatedFieldCount);

    [LoggerMessage(5022, LogLevel.Debug, "Rule operation {Operation} succeeded. RuleId: {RuleId}")]
    public static partial void RuleDeleted(
        this ILogger logger,
        string operation,
        string ruleId);

    [LoggerMessage(5023, LogLevel.Warning, "Rule operation {Operation} failed validation. RuleId: {RuleId}; ErrorCount: {ErrorCount}; ValidationErrors: {ValidationErrors}")]
    public static partial void RuleValidationFailed(
        this ILogger logger,
        string operation,
        string? ruleId,
        int errorCount,
        string validationErrors);

    [LoggerMessage(5024, LogLevel.Debug, "Rule operation {Operation} completed. RequestedCount: {RequestedCount}; SuccessCount: {SuccessCount}; FailureCount: {FailureCount}; SensorName: {SensorName}")]
    public static partial void BulkRuleOperationSucceeded(
        this ILogger logger,
        string operation,
        int requestedCount,
        int successCount,
        int failureCount,
        string? sensorName);

    [LoggerMessage(5025, LogLevel.Warning, "Rule operation {Operation} completed. RequestedCount: {RequestedCount}; SuccessCount: {SuccessCount}; FailureCount: {FailureCount}; SensorName: {SensorName}; FailedIdSample: {FailedIdSample}; OmittedFailureCount: {OmittedFailureCount}")]
    public static partial void BulkRuleOperationCompletedWithFailures(
        this ILogger logger,
        string operation,
        int requestedCount,
        int successCount,
        int failureCount,
        string? sensorName,
        string failedIdSample,
        int omittedFailureCount);

    [LoggerMessage(5050, LogLevel.Warning, "Elasticsearch health probe returned an invalid response. HttpStatusCode: {HttpStatusCode}; FailureType: {FailureType}")]
    public static partial void ElasticsearchHealthInvalidResponse(
        this ILogger logger,
        int? httpStatusCode,
        string? failureType);

    [LoggerMessage(5051, LogLevel.Warning, "Elasticsearch health probe failed.")]
    public static partial void ElasticsearchHealthProbeFailed(
        this ILogger logger,
        Exception exception);
}
