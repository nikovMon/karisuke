using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbConsumer.Application;

public sealed class TbMessageHandler : IRabbitMqMessageHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IProjectionMapperClient _projectionMapper;
    private readonly EmbedderInputMessageBuilder _embedderInputMessageBuilder;
    private readonly IRabbitMqPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TbMessageHandler> _logger;

    public TbMessageHandler(
        IProjectionMapperClient projectionMapper,
        EmbedderInputMessageBuilder embedderInputMessageBuilder,
        IRabbitMqPublisher publisher,
        TimeProvider timeProvider,
        ILogger<TbMessageHandler> logger)
    {
        _projectionMapper = projectionMapper;
        _embedderInputMessageBuilder = embedderInputMessageBuilder;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public async Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var started = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Failure;
        var error = TelemetryErrorCategory.Unknown;
        var processingState = new HandlerTelemetryState();

        PipelineTelemetry.RecordPayloadSize(PipelineStage.TbConsumer, PipelineDirection.Ingress, message.Body.LongLength);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(message.Headers, out var elapsedSeconds, _timeProvider))
        {
            PipelineTelemetry.RecordEndToEndDuration(PipelineStage.TbConsumer, elapsedSeconds);
        }

        try
        {
            TbConsumerInputDto input;
            using (var validationActivity = StartStageActivity("validate"))
            {
                try
                {
                    input = JsonSerializer.Deserialize<TbConsumerInputDto>(message.BodyAsUtf8(), SerializerOptions)!;
                }
                catch (JsonException ex)
                {
                    outcome = TelemetryOutcome.Rejected;
                    error = TelemetryErrorCategory.Serialization;
                    validationActivity.SetTelemetryError(error, ex);
                    var deserializationError = $"Json deserialization failed: {ex.Message}";
                    _logger.DeserializationRejected(deserializationError);
                    return RabbitMqMessageProcessingResult.Failure(deserializationError);
                }
                catch (OperationCanceledException ex)
                {
                    outcome = TelemetryOutcome.Cancelled;
                    error = TelemetryErrorCategory.Cancelled;
                    validationActivity.SetTelemetryError(error, ex, recordException: false);
                    throw;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    outcome = TelemetryOutcome.Rejected;
                    error = TelemetryErrorCategory.Serialization;
                    validationActivity.SetTelemetryError(error, ex);
                    var deserializationError = $"Unhandled deserialization error: {ex.Message}";
                    _logger.DeserializationRejected(deserializationError);
                    return RabbitMqMessageProcessingResult.Failure(deserializationError);
                }

                try
                {
                    if (input is null)
                    {
                        outcome = TelemetryOutcome.Rejected;
                        error = TelemetryErrorCategory.Serialization;
                        validationActivity.SetTelemetryError(error);
                        const string deserializationError = "Deserialization produced null.";
                        _logger.DeserializationRejected(deserializationError);
                        return RabbitMqMessageProcessingResult.Failure(deserializationError);
                    }

                    var matchedAlgorithms = input.Metadata.MissionMetadata.Overlay.AlgorithmNames;
                    var algorithmNameText = matchedAlgorithms is { Count: > 0 }
                        ? string.Join(",", matchedAlgorithms)
                        : null;
                    validationActivity.AddPipelineContext(
                        taskId: input.Metadata.TaskId,
                        requestId: input.RequestId,
                        imageId: input.Metadata.MissionMetadata.Overlay.ImageId,
                        ruleId: input.Metadata.MissionMetadata.Overlay.RuleId,
                        tenantId: input.Metadata.MissionMetadata.TenantId,
                        algorithmName: algorithmNameText);

                    var validationError = Validate(input);
                    if (validationError is not null)
                    {
                        outcome = TelemetryOutcome.Rejected;
                        error = TelemetryErrorCategory.Validation;
                        validationActivity.SetTelemetryError(error);
                        _logger.MessageRejected(validationError);
                        return RabbitMqMessageProcessingResult.Failure(validationError);
                    }

                    if (validationActivity?.IsAllDataRequested == true)
                    {
                        validationActivity.SetTag(
                            "imaging_pipeline.pipeline.tile.count",
                            input.Tiles.Count);
                    }
                    validationActivity.SetTelemetrySuccess();
                }
                catch (OperationCanceledException ex)
                {
                    outcome = TelemetryOutcome.Cancelled;
                    error = TelemetryErrorCategory.Cancelled;
                    validationActivity.SetTelemetryError(error, ex, recordException: false);
                    throw;
                }
                catch (Exception ex)
                {
                    if (validationActivity?.Status != ActivityStatusCode.Error)
                    {
                        outcome = TelemetryOutcome.Retry;
                        error = TelemetryErrorCategory.Handler;
                        validationActivity.SetTelemetryError(error, ex);
                    }

                    throw;
                }
            }

            var overlay = input.Metadata.MissionMetadata.Overlay;
            Activity.Current.AddPipelineContext(
                taskId: input.Metadata.TaskId,
                requestId: input.RequestId,
                imageId: overlay.ImageId,
                ruleId: overlay.RuleId,
                tenantId: input.Metadata.MissionMetadata.TenantId,
                algorithmName: string.Join(",", overlay.AlgorithmNames));

            var result = await HandleValidatedMessageAsync(input, message, cancellationToken, processingState);
            outcome = processingState.Outcome;
            error = processingState.Error;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            throw;
        }
        catch
        {
            if (processingState.Error != TelemetryErrorCategory.None)
            {
                outcome = processingState.Outcome;
                error = processingState.Error;
            }

            if (outcome == TelemetryOutcome.Failure && error == TelemetryErrorCategory.Unknown)
            {
                outcome = TelemetryOutcome.Retry;
                error = TelemetryErrorCategory.Handler;
            }

            throw;
        }
        finally
        {
            if (processingState.PublishedCount > 0)
            {
                PipelineTelemetry.RecordMessage(
                    PipelineStage.TbConsumer,
                    PipelineDirection.Egress,
                    TelemetryOutcome.Success,
                    count: processingState.PublishedCount);
            }

            if (processingState.Outcome == TelemetryOutcome.Success)
            {
                PipelineTelemetry.RecordFanOut(PipelineStage.TbConsumer, processingState.PublishedCount);
            }
            PipelineTelemetry.RecordMessage(PipelineStage.TbConsumer, PipelineDirection.Ingress, outcome, error);
            PipelineTelemetry.RecordStageDuration(
                PipelineStage.TbConsumer,
                TelemetryTiming.ElapsedSeconds(started),
                outcome,
                error);
        }
    }

    private async Task<RabbitMqMessageProcessingResult> HandleValidatedMessageAsync(
        TbConsumerInputDto input,
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken,
        HandlerTelemetryState telemetryState)
    {
        var matchedAlgorithms = input.Metadata.MissionMetadata.Overlay.AlgorithmNames;
        var algorithmNames = matchedAlgorithms
            .Select(algorithm => algorithm.ToString())
            .ToList();
        var algorithmNameText = string.Join(",", algorithmNames);
        var overlay = input.Metadata.MissionMetadata.Overlay;
        using var pipelineScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
            TaskId: input.Metadata.TaskId,
            RequestId: input.RequestId,
            ImageId: overlay.ImageId,
            RuleId: overlay.RuleId,
            TenantId: input.Metadata.MissionMetadata.TenantId,
            AlgorithmName: algorithmNameText));
        using var correlationBaggage = PipelineCorrelationBaggage.Push(
            new PipelineCorrelationContext(
                TaskId: input.Metadata.TaskId,
                RequestId: input.RequestId,
                ImageId: overlay.ImageId,
                RuleId: overlay.RuleId,
                TenantId: input.Metadata.MissionMetadata.TenantId,
                AlgorithmName: algorithmNameText));

        PipelineTelemetry.RecordBatchSize(PipelineStage.TbConsumer, PipelineItem.Tile, input.Tiles.Count);

        var outgoingHeaders = message.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal);
        outgoingHeaders["algorithm_name"] = algorithmNameText;
        var headers = new ReadOnlyDictionary<string, object?>(outgoingHeaders);

        var tileCorners = input.Tiles
            .SelectMany(tile => new IReadOnlyList<double>[]
            {
                [tile.Roi[0], tile.Roi[1]], // top-left
                [tile.Roi[2], tile.Roi[1]], // top-right
                [tile.Roi[2], tile.Roi[3]], // bottom-right
                [tile.Roi[0], tile.Roi[3]], // bottom-left
            })
            .ToList();

        IReadOnlyList<IReadOnlyList<double>> batchMapped;
        using (var projectionActivity = StartStageActivity("projection"))
        {
            try
            {
                projectionActivity.AddPipelineContext(
                    taskId: input.Metadata.TaskId,
                    requestId: input.RequestId,
                    imageId: overlay.ImageId,
                    ruleId: overlay.RuleId,
                    tenantId: input.Metadata.MissionMetadata.TenantId,
                    algorithmName: algorithmNameText);
                if (projectionActivity?.IsAllDataRequested == true)
                {
                    projectionActivity.SetTag(
                        "imaging_pipeline.pipeline.tile.count",
                        input.Tiles.Count);
                }
                batchMapped = await _projectionMapper.ProcessBatchAsync(
                    overlay.ImageId,
                    tileCorners,
                    cancellationToken);

                if (batchMapped.Count != input.Tiles.Count * 4)
                {
                    var failureReason =
                        $"Projection mapper returned {batchMapped.Count} results for {input.Tiles.Count} requested tiles.";
                    telemetryState.SetOutcome(TelemetryOutcome.Retry, TelemetryErrorCategory.Dependency);
                    projectionActivity.SetTelemetryError(TelemetryErrorCategory.Dependency);
                    _logger.ProjectionResultCountMismatchScheduledForRetry(
                        input.Tiles.Count,
                        batchMapped.Count);
                    return RabbitMqMessageProcessingResult.RetryableFailure(failureReason);
                }

                if (projectionActivity?.IsAllDataRequested == true)
                {
                    projectionActivity.SetTag(
                        "imaging_pipeline.pipeline.coordinate.count",
                        batchMapped.Count);
                }
                projectionActivity.SetTelemetrySuccess();
            }
            catch (OperationCanceledException ex)
            {
                telemetryState.SetOutcome(TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled);
                projectionActivity.SetTelemetryError(
                    TelemetryErrorCategory.Cancelled,
                    ex,
                    recordException: false);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                telemetryState.SetOutcome(TelemetryOutcome.Retry, TelemetryErrorCategory.Dependency);
                projectionActivity.SetTelemetryError(
                    TelemetryErrorCategory.Dependency,
                    ex,
                    recordException: false);
                // RabbitMQ's handler boundary owns the exception-bearing error log.
                _logger.ProjectionScheduledForRetry(input.Tiles.Count);
                throw;
            }
        }

        var mappedCoordinateCount = batchMapped.Sum(coordinates => coordinates.Count / 2);
        PipelineTelemetry.RecordBatchSize(PipelineStage.TbConsumer, PipelineItem.Coordinate, mappedCoordinateCount);

        var publishedCount = 0;
        using var buildActivity = StartStageActivity("build");
        try
        {
            buildActivity.AddPipelineContext(
                taskId: input.Metadata.TaskId,
                requestId: input.RequestId,
                imageId: overlay.ImageId,
                ruleId: overlay.RuleId,
                tenantId: input.Metadata.MissionMetadata.TenantId,
                algorithmName: algorithmNameText);
            if (buildActivity?.IsAllDataRequested == true)
            {
                buildActivity.SetTag("imaging_pipeline.pipeline.tile.count", input.Tiles.Count);
            }

            var envelopes = _embedderInputMessageBuilder.Build(input, batchMapped, _timeProvider.GetUtcNow());
            for (var i = 0; i < envelopes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var envelope = envelopes[i];
                var body = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
                PipelineTelemetry.RecordPayloadSize(PipelineStage.TbConsumer, PipelineDirection.Egress, body.LongLength);
                var outgoing = new RabbitMqMessageEnvelope(
                    MessageId: Guid.NewGuid().ToString("N"),
                    Body: body,
                    Headers: headers,
                    CorrelationId: message.CorrelationId ?? message.MessageId);

                try
                {
                    await _publisher.PublishToOutputAsync(outgoing, cancellationToken);
                    publishedCount++;
                    telemetryState.PublishedCount = publishedCount;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    telemetryState.SetOutcome(TelemetryOutcome.Retry, TelemetryErrorCategory.Publish);
                    buildActivity.SetTelemetryError(
                        TelemetryErrorCategory.Publish,
                        ex,
                        recordException: false);
                    // RabbitMQ's handler boundary owns the exception-bearing error log.
                    _logger.OutputPublishScheduledForRetry(publishedCount, input.Tiles.Count);
                    throw;
                }
            }

            buildActivity.SetTelemetrySuccess();
            telemetryState.SetOutcome(TelemetryOutcome.Success, TelemetryErrorCategory.None);
        }
        catch (OperationCanceledException ex)
        {
            telemetryState.SetOutcome(TelemetryOutcome.Cancelled, TelemetryErrorCategory.Cancelled);
            buildActivity.SetTelemetryError(
                TelemetryErrorCategory.Cancelled,
                ex,
                recordException: false);
            throw;
        }
        catch (Exception ex)
        {
            if (telemetryState.Error == TelemetryErrorCategory.None)
            {
                telemetryState.SetOutcome(TelemetryOutcome.Retry, TelemetryErrorCategory.Handler);
                buildActivity.SetTelemetryError(TelemetryErrorCategory.Handler, ex);
            }

            throw;
        }
        finally
        {
            if (buildActivity?.IsAllDataRequested == true)
            {
                buildActivity.SetTag("imaging_pipeline.pipeline.output.count", publishedCount);
            }
        }

        _logger.MessageProcessed(input.Tiles.Count, mappedCoordinateCount, publishedCount);
        return RabbitMqMessageProcessingResult.Success();
    }

    private static string? Validate(TbConsumerInputDto input)
    {
        if (string.IsNullOrWhiteSpace(input.Metadata.MissionMetadata.TenantId))
        {
            return "Validation failed: tenantId is missing or empty.";
        }

        if (input.Tiles is not { Count: > 0 })
        {
            return "Validation failed: Tiles batch is null or empty.";
        }

        var matchedAlgorithms = input.Metadata.MissionMetadata.Overlay.AlgorithmNames;
        return matchedAlgorithms is not { Count: > 0 }
               || matchedAlgorithms.Any(algorithm => !Enum.IsDefined(algorithm))
               || matchedAlgorithms.Distinct().Count() != matchedAlgorithms.Count
            ? $"Validation failed: algorithm_name must contain one or more unique algorithms. Valid algorithms are: {string.Join(", ", Enum.GetNames<AlgorithmName>())}"
            : null;
    }

    private static Activity? StartStageActivity(string operation)
    {
        var spanName = operation switch
        {
            "validate" => "tb_consumer.validate",
            "projection" => "tb_consumer.projection",
            "build" => "tb_consumer.build",
            _ => "tb_consumer.stage"
        };
        var activity = TelemetrySources.TbConsumer.StartActivity(spanName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "tb_consumer");
            activity.SetTag("imaging_pipeline.pipeline.operation", operation);
        }
        return activity;
    }

    private sealed class HandlerTelemetryState
    {
        public TelemetryOutcome Outcome { get; private set; } = TelemetryOutcome.Failure;

        public TelemetryErrorCategory Error { get; private set; } = TelemetryErrorCategory.None;

        public int PublishedCount { get; set; }

        public void SetOutcome(TelemetryOutcome outcome, TelemetryErrorCategory error)
        {
            Outcome = outcome;
            Error = error;
        }
    }
}
