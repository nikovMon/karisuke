using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Gateway;

internal static partial class GatewayLog
{
    [LoggerMessage(2000, LogLevel.Information, "Gateway RabbitMQ consumer is starting.")]
    public static partial void ConsumerStarting(this ILogger logger);

    [LoggerMessage(2001, LogLevel.Information, "Gateway RabbitMQ consumer has stopped.")]
    public static partial void ConsumerStopped(this ILogger logger);

    [LoggerMessage(2002, LogLevel.Warning, "Gateway RabbitMQ consumer exited unexpectedly; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartScheduled(this ILogger logger, double restartDelaySeconds);

    [LoggerMessage(2003, LogLevel.Warning, "Gateway RabbitMQ consumer failed; restarting in {RestartDelaySeconds} seconds.")]
    public static partial void ConsumerRestartAfterFailure(
        this ILogger logger,
        Exception exception,
        double restartDelaySeconds);

    [LoggerMessage(2010, LogLevel.Information, "Gateway processed an input message: {RulesEvaluated} rules evaluated, {RulesMatched} rules matched, and {OutputCount} tenant tasks built.")]
    public static partial void MessageProcessed(
        this ILogger logger,
        int rulesEvaluated,
        int rulesMatched,
        int outputCount);

    [LoggerMessage(2011, LogLevel.Warning, "Gateway rejected an input message with validation code {ErrorCode}: {ValidationError}")]
    public static partial void MessageRejected(
        this ILogger logger,
        string errorCode,
        string validationError);

    [LoggerMessage(2012, LogLevel.Warning, "Gateway could not process an input message; RabbitMQ will retry it. Error category: {ErrorCategory}")]
    public static partial void MessageScheduledForRetry(
        this ILogger logger,
        Exception exception,
        string errorCategory);

    [LoggerMessage(2013, LogLevel.Information, "Gateway excluded {FilteredRuleCount} rules from matching because image photo age {ImageAgeDays} days exceeded the configured maximum of {MaxPhotoAgeDays} days. PhotoTime: {PhotoTime}")]
    public static partial void OldPhotoRulesFiltered(
        this ILogger logger,
        int filteredRuleCount,
        double imageAgeDays,
        int maxPhotoAgeDays,
        DateTimeOffset photoTime);

    [LoggerMessage(2014, LogLevel.Warning, "Gateway input image {ImageId} has no areaOfInterest; processing continues without area metadata.")]
    public static partial void MissingAreaOfInterest(this ILogger logger, string imageId);

    [LoggerMessage(2020, LogLevel.Information, "Gateway initialized its active-rule cache with {EntryCount} rules; {SkippedCount} invalid rules were skipped.")]
    public static partial void RuleCacheInitialized(
        this ILogger logger,
        int entryCount,
        int skippedCount);

    [LoggerMessage(2021, LogLevel.Debug, "Gateway refreshed its active-rule cache with {EntryCount} rules; {SkippedCount} invalid rules were skipped.")]
    public static partial void RuleCacheRefreshed(
        this ILogger logger,
        int entryCount,
        int skippedCount);

    [LoggerMessage(2022, LogLevel.Warning, "Gateway active-rule refresh failed; retaining the last valid snapshot containing {RetainedEntryCount} rules.")]
    public static partial void RuleCacheRefreshFailed(
        this ILogger logger,
        Exception exception,
        int retainedEntryCount);

    [LoggerMessage(2023, LogLevel.Warning, "Gateway skipped {SkippedCount} invalid rules while building the active-rule snapshot. FailedRuleSample: {FailedRuleSample}; OmittedFailureCount: {OmittedFailureCount}")]
    public static partial void RuleCacheRulesSkipped(
        this ILogger logger,
        Exception exception,
        int skippedCount,
        string failedRuleSample,
        int omittedFailureCount);
}
