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
    private readonly IRabbitMqPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TbMessageHandler> _logger;

    public TbMessageHandler(
        IProjectionMapperClient projectionMapper,
        IRabbitMqPublisher publisher,
        TimeProvider timeProvider,
        ILogger<TbMessageHandler> logger)
    {
        _projectionMapper = projectionMapper;
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

                    validationActivity.AddPipelineContext(
                        taskId: input.TaskId,
                        requestId: input.RequestId,
                        imageId: input.MissionMetadata.Overlay.ImageId,
                        ruleId: input.MissionMetadata.Overlay.RuleId,
                        tenantId: input.MissionMetadata.TenantId,
                        algorithmName: input.MissionMetadata.Overlay.AlgorithmName.ToString());

                    var validationError = Validate(input);
                    if (validationError is not null)
                    {
                        outcome = TelemetryOutcome.Rejected;
                        error = TelemetryErrorCategory.Validation;
                        validationActivity.SetTelemetryError(error);
                        _logger.MessageRejected(validationError);
                        return RabbitMqMessageProcessingResult.Failure(validationError);
                    }

                    var matchedAlgorithm = input.MissionMetadata.Overlay.AlgorithmName;
                    if (!Enum.IsDefined(typeof(AlgorithmName), matchedAlgorithm))
                    {
                        var invalidAlgorithmError =
                            $"Validation failed: invalid algorithm '{matchedAlgorithm}'. Valid algorithms are: {string.Join(", ", Enum.GetNames<AlgorithmName>())}";
                        outcome = TelemetryOutcome.Rejected;
                        error = TelemetryErrorCategory.Validation;
                        validationActivity.SetTelemetryError(error);
                        _logger.MessageRejected(invalidAlgorithmError);
                        return RabbitMqMessageProcessingResult.Failure(invalidAlgorithmError);
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

            PipelineTelemetry.RecordFanOut(PipelineStage.TbConsumer, processingState.PublishedCount);
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
        var matchedAlgorithm = input.MissionMetadata.Overlay.AlgorithmName;
        var overlay = input.MissionMetadata.Overlay;
        using var pipelineScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
            TaskId: input.TaskId,
            RequestId: input.RequestId,
            ImageId: overlay.ImageId,
            RuleId: overlay.RuleId,
            TenantId: input.MissionMetadata.TenantId,
            AlgorithmName: matchedAlgorithm.ToString()));
        using var correlationBaggage = PipelineCorrelationBaggage.Push(
            new PipelineCorrelationContext(
                TaskId: input.TaskId,
                RequestId: input.RequestId,
                ImageId: overlay.ImageId,
                RuleId: overlay.RuleId,
                TenantId: input.MissionMetadata.TenantId,
                AlgorithmName: matchedAlgorithm.ToString()));

        PipelineTelemetry.RecordBatchSize(PipelineStage.TbConsumer, PipelineItem.Tile, input.Tiles.Count);

        var outgoingHeaders = message.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal);
        outgoingHeaders["algorithm_name"] = matchedAlgorithm.ToString();
        var headers = new ReadOnlyDictionary<string, object?>(outgoingHeaders);

        var tilesRois = input.Tiles
            .Select(t => (IReadOnlyList<double>)t.Roi)
            .ToList();

        IReadOnlyList<IReadOnlyList<double>> batchMapped;
        using (var projectionActivity = StartStageActivity("projection"))
        {
            try
            {
                projectionActivity.AddPipelineContext(
                    taskId: input.TaskId,
                    requestId: input.RequestId,
                    imageId: overlay.ImageId,
                    ruleId: overlay.RuleId,
                    tenantId: input.MissionMetadata.TenantId,
                    algorithmName: matchedAlgorithm.ToString());
                if (projectionActivity?.IsAllDataRequested == true)
                {
                    projectionActivity.SetTag(
                        "imaging_pipeline.pipeline.tile.count",
                        input.Tiles.Count);
                }
                batchMapped = await _projectionMapper.ProcessBatchAsync(
                    overlay.ImageId,
                    tilesRois,
                    cancellationToken);

                if (batchMapped.Count != input.Tiles.Count)
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
                taskId: input.TaskId,
                requestId: input.RequestId,
                imageId: overlay.ImageId,
                ruleId: overlay.RuleId,
                tenantId: input.MissionMetadata.TenantId,
                algorithmName: matchedAlgorithm.ToString());
            if (buildActivity?.IsAllDataRequested == true)
            {
                buildActivity.SetTag("imaging_pipeline.pipeline.tile.count", input.Tiles.Count);
            }
            for (var i = 0; i < input.Tiles.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var tile = input.Tiles[i];

                double? lon = null;
                double? lat = null;
                var coordsList = new List<double[]>();

                if (batchMapped[i] is { Count: >= 2 } mapped)
                {
                    lon = mapped[0];
                    lat = mapped[1];

                    for (var j = 0; j < mapped.Count - 1; j += 2)
                    {
                        coordsList.Add([mapped[j], mapped[j + 1]]);
                    }
                }

                var tileCoordinates = new PolygonDto
                {
                    Coordinates = coordsList.ToArray()
                };

                double? tilesSizeMeters = null;
                if (tile.Roi.Length >= 4 && overlay.ResolutionMPerPx > 0)
                {
                    var widthPx = System.Math.Abs(tile.Roi[2] - tile.Roi[0]);
                    var heightPx = System.Math.Abs(tile.Roi[3] - tile.Roi[1]);
                    tilesSizeMeters = System.Math.Max(widthPx, heightPx) * overlay.ResolutionMPerPx;
                }

                var embedderInput = new EmbedderInput
                {
                TileId = tile.TileIndex.ToString(),
                Gid = input.RequestId,
                ImagePath = tile.Uri,
                Sensor = overlay.SensorName,
                ImagingTime = overlay.ImageTime,
                Resolution = overlay.ResolutionMPerPx,
                TenantId = input.MissionMetadata.TenantId,
                Algorithms = new List<string> { matchedAlgorithm.ToString() },
                TileCoordinates = tileCoordinates,
                Lon = lon,
                Lat = lat,
                TilesSizeMeters = tilesSizeMeters,
                RequestTime = _timeProvider.GetUtcNow().UtcDateTime,
                };

                var envelope = new EmbedderInputDto
                {
                FrameMetadata = input.FrameMetadata,
                ModelMetadata = input.ModelMetadata,
                FocusedPxWkt = input.FocusedPxWkt,
                MissionMetadata = input.MissionMetadata,
                RequestId = input.RequestId,
                TaskId = input.TaskId,
                EmbedderInput = embedderInput
                };

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
        if (string.IsNullOrWhiteSpace(input.MissionMetadata.TenantId))
        {
            return "Validation failed: tenantId is missing or empty.";
        }

        return input.Tiles is not { Count: > 0 }
            ? "Validation failed: Tiles batch is null or empty."
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
