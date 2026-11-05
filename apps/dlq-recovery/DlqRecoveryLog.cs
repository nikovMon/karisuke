using Microsoft.Extensions.Logging;

namespace ImagingPipeline.DlqRecovery;

internal static partial class DlqRecoveryLog
{
    [LoggerMessage(6000, LogLevel.Information, "DLQ recovery run {RunId} started; dry run: {DryRun}.")]
    internal static partial void Started(ILogger logger, string runId, bool dryRun);

    [LoggerMessage(6001, LogLevel.Information, "DLQ recovery mapping {Mapping}: {ReadyCount} ready messages; dry run performs no consumption or publication.")]
    internal static partial void Inspected(ILogger logger, string mapping, uint readyCount);

    [LoggerMessage(6002, LogLevel.Information, "DLQ recovery mapping {Mapping}: scanned {Scanned}, replay published {Replayed}, retained {Retained}, stopped for {Reason}.")]
    internal static partial void Completed(ILogger logger, string mapping, int scanned, int replayed, int retained, string reason);

    [LoggerMessage(6003, LogLevel.Error, "DLQ recovery mapping {Mapping} stopped after a broker operation failed ({FailureType}); unsettled source deliveries remain unacknowledged.")]
    internal static partial void Failed(ILogger logger, string mapping, string failureType);

    [LoggerMessage(6004, LogLevel.Information, "DLQ recovery run {RunId} reached its deadline or received shutdown.")]
    internal static partial void Stopping(ILogger logger, string runId);

    [LoggerMessage(6005, LogLevel.Error, "DLQ recovery could not start: {Reason}.")]
    internal static partial void InvalidConfiguration(ILogger logger, string reason);

    [LoggerMessage(6006, LogLevel.Warning, "DLQ recovery failure log could not be emitted ({FailureType}); message settlement is unaffected.")]
    internal static partial void LoggingFailed(ILogger logger, string failureType);
}
