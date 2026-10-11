using System.Diagnostics;
using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.UnifiedGateway.Dispatch;

namespace ImagingPipeline.UnifiedGateway.Source;

/// <summary>
/// Telemetry for handling one source message: begin with the message, report how it ended,
/// dispose. Owns the stage metrics, the log scopes and the outcome logs, so the handler stays
/// free of telemetry. A message disposed without an outcome failed unexpectedly and counts as a
/// retryable handler failure.
/// </summary>
internal sealed class SourceMessageTelemetry : IDisposable
{
    private readonly ILogger _logger;
    private readonly PipelineStageScope _stage;
    private readonly IDisposable? _messageScope;
    private IDisposable? _imageScope;

    private SourceMessageTelemetry(RabbitMqMessageEnvelope message, ILogger logger)
    {
        _logger = logger;
        _stage = PipelineStageScope.Begin(PipelineStage.UnifiedGateway, message.Body.LongLength);
        _messageScope = logger.BeginTelemetryScope(new TelemetryLogContext(MessageId: message.MessageId));
    }

    public static SourceMessageTelemetry Begin(RabbitMqMessageEnvelope message, ILogger logger) => new(message, logger);

    /// <summary>The message breaks its contract and will be dead-lettered.</summary>
    public void Rejected(InvalidInputMessageException exception)
    {
        _stage.Rejected(TelemetryErrorCategory.Validation);
        using (_logger.BeginScope(new KeyValuePair<string, object?>[] { new(TelemetryAttributeNames.ErrorCode, exception.ErrorCode) }))
        {
            _logger.SourceMessageRejected(exception);
        }
    }

    /// <summary>From here on, every log carries the image's identifiers.</summary>
    public void Parsed(GatewayInputMessage image)
    {
        var context = new TelemetryLogContext(ImageId: image.ImageId, AreaName: image.AreaOfInterest, SensorName: image.SensorName);
        Activity.Current.AddPipelineContext(context);
        _imageScope = _logger.BeginTelemetryScope(context);
    }

    /// <summary>Logs how many units were sent and how many failed, and classifies the message.</summary>
    public void Completed(IReadOnlyList<DispatchOutcome> outcomes, int invalidUnitCount, RabbitMqMessageProcessingResult result)
    {
        var delivered = outcomes.Count(outcome => outcome.Status == DispatchStatus.Delivered);
        for (var index = 0; index < delivered; index++)
        {
            _stage.MessagePublished();
        }

        var outcome = result.IsSuccess ? TelemetryOutcome.Success
            : result.FailureAction == RabbitMqMessageFailureAction.Retry ? TelemetryOutcome.Retry
            : TelemetryOutcome.Rejected;
        switch (outcome)
        {
            case TelemetryOutcome.Success:
                _stage.Succeeded();
                break;
            case TelemetryOutcome.Retry:
                _stage.Retryable(TelemetryErrorCategory.Dependency);
                break;
            default:
                _stage.Rejected(TelemetryErrorCategory.Validation);
                break;
        }

        using (_logger.BeginScope(new KeyValuePair<string, object?>[]
               {
                   new(TelemetryAttributeNames.PipelineOutcome, outcome.Value()),
                   new(TelemetryAttributeNames.DispatchUnitCount, outcomes.Count + invalidUnitCount),
                   new(TelemetryAttributeNames.DispatchFailedCount, outcomes.Count - delivered + invalidUnitCount)
               }))
        {
            _logger.SourceMessageProcessed();
        }
    }

    public void Dispose()
    {
        _stage.Faulted();
        _stage.Dispose();
        _imageScope?.Dispose();
        _messageScope?.Dispose();
    }
}
