using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Messaging;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

namespace ImagingPipeline.TbConsumer.Application;

public sealed class TbMessageHandler : IRabbitMqMessageHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new() { PropertyNameCaseInsensitive = true };

    private readonly IProjectionMapperClient _projectionMapper;
    private readonly EmbedderInputMessageBuilder _embedderInputMessageBuilder;
    private readonly IRabbitMqPublisher _publisher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<TbMessageHandler> _logger;
    private readonly IMessageTraceContextPropagator _traceContextPropagator;

    public TbMessageHandler(
        IProjectionMapperClient projectionMapper,
        EmbedderInputMessageBuilder embedderInputMessageBuilder,
        IRabbitMqPublisher publisher,
        TimeProvider timeProvider,
        ILogger<TbMessageHandler> logger,
        IMessageTraceContextPropagator traceContextPropagator)
    {
        _projectionMapper = projectionMapper;
        _embedderInputMessageBuilder = embedderInputMessageBuilder;
        _publisher = publisher;
        _timeProvider = timeProvider;
        _logger = logger;
        _traceContextPropagator = traceContextPropagator;
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

        try
        {
            TbConsumerInputDto? input;
            try
            {
                input = JsonSerializer.Deserialize<TbConsumerInputDto>(
                    message.BodyAsUtf8(),
                    SerializerOptions);
            }
            catch (JsonException ex)
            {
                outcome = TelemetryOutcome.Rejected;
                error = TelemetryErrorCategory.Serialization;
                var deserializationError = $"Json deserialization failed: {ex.Message}";
                _logger.DeserializationRejected(deserializationError);
                return RabbitMqMessageProcessingResult.Failure(deserializationError);
            }
            catch (OperationCanceledException)
            {
                outcome = TelemetryOutcome.Cancelled;
                error = TelemetryErrorCategory.Cancelled;
                throw;
            }
            catch (Exception ex)
            {
                outcome = TelemetryOutcome.Rejected;
                error = TelemetryErrorCategory.Serialization;
                var deserializationError = $"Unhandled deserialization error: {ex.Message}";
                _logger.DeserializationRejected(deserializationError);
                return RabbitMqMessageProcessingResult.Failure(deserializationError);
            }

            if (input is null)
            {
                outcome = TelemetryOutcome.Rejected;
                error = TelemetryErrorCategory.Serialization;
                const string deserializationError = "Deserialization produced null.";
                _logger.DeserializationRejected(deserializationError);
                return RabbitMqMessageProcessingResult.Failure(deserializationError);
            }

            var telemetryContext = CreateTelemetryContext(input);
            using var pipelineScope = _logger.BeginTelemetryScope(telemetryContext);
            Activity.Current.AddPipelineContext(
                taskId: telemetryContext.TaskId,
                requestId: telemetryContext.RequestId,
                imageId: telemetryContext.ImageId,
                ruleId: telemetryContext.RuleId,
                tenantId: telemetryContext.TenantId,
                algorithmName: telemetryContext.AlgorithmName,
                areaName: telemetryContext.AreaName,
                sensorName: telemetryContext.SensorName);

            var validationFailure = Validate(input);
            if (validationFailure is not null)
            {
                outcome = TelemetryOutcome.Rejected;
                error = TelemetryErrorCategory.Validation;
                _logger.MessageRejected(
                    validationFailure.Code,
                    validationFailure.Message,
                    validationFailure.TilesAmount,
                    validationFailure.BatchTilesAmount,
                    validationFailure.ActualBatchTileCount,
                    validationFailure.OffendingTileIndex);
                return RabbitMqMessageProcessingResult.Failure(validationFailure.Message);
            }

            if (!IsRetry(message.Headers))
            {
                var overlay = input.Metadata.MissionMetadata.Overlay;
                var algorithmNameText = string.Join(",", overlay.AlgorithmNames.Select(a => a.ToString()));
                WorkloadTelemetry.RecordTilesReceived(
                    overlay.RuleId,
                    input.Metadata.MissionMetadata.TenantId,
                    overlay.AreaOfInterest,
                    overlay.SensorName,
                    algorithmNameText,
                    input.Tiles.Count);
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

        PipelineTelemetry.RecordBatchSize(PipelineStage.TbConsumer, PipelineItem.Tile, input.Tiles.Count);

        var outgoingHeaders = FindAirMessageHeaders.Forward(
            message.Headers,
            algorithmNameText);
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
                    algorithmName: algorithmNameText,
                    areaName: overlay.AreaOfInterest,
                    sensorName: overlay.SensorName);
                if (projectionActivity?.IsAllDataRequested == true)
                {
                    projectionActivity.SetTag(
                        "findair.tile.count",
                        input.Tiles.Count);
                }
                var useRegistrationEndpoint =
                    string.Equals(overlay.GridType, "MSP", StringComparison.Ordinal) &&
                    overlay.ImageId.StartsWith("SHR", StringComparison.OrdinalIgnoreCase);

                batchMapped = useRegistrationEndpoint
                    ? await _projectionMapper.ProcessBatchByRegistrationAsync(
                        overlay.ImageId,
                        tileCorners,
                        overlay.GridType,
                        overlay.GridUri,
                        cancellationToken)
                    : await _projectionMapper.ProcessBatchAsync(
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
                        "findair.coordinate.count",
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
        using var buildActivity = StartEmbedderBatchActivity(input.Tiles.Count);
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
                buildActivity.SetTag("findair.tile.count", input.Tiles.Count);
            }

            if (buildActivity is not null)
            {
                _traceContextPropagator.Inject(outgoingHeaders, buildActivity.Context);
            }
            else
            {
                _traceContextPropagator.InjectCurrent(outgoingHeaders);
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
                    Headers: headers);

                try
                {
                    using (SuppressInstrumentationScope.Begin())
                    {
                        await _publisher.PublishToOutputAsync(outgoing, cancellationToken);
                    }

                    publishedCount++;
                    telemetryState.PublishedCount = publishedCount;
                    WorkloadTelemetry.RecordTilePublishAttempt(
                        TelemetryOutcome.Success,
                        overlay.RuleId,
                        input.Metadata.MissionMetadata.TenantId,
                        overlay.AreaOfInterest,
                        overlay.SensorName,
                        algorithmNameText);
                    using var tileScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
                        TaskId: input.Metadata.TaskId,
                        RequestId: input.RequestId,
                        ImageId: overlay.ImageId,
                        RuleId: overlay.RuleId,
                        TenantId: input.Metadata.MissionMetadata.TenantId,
                        AlgorithmName: algorithmNameText,
                        AreaName: overlay.AreaOfInterest,
                        SensorName: overlay.SensorName,
                        TileId: envelope.EmbedderInput.TileId,
                        TileIndex: input.Tiles[i].TileIndex));
                    _logger.TileSentToEmbedder(
                        envelope.EmbedderInput.TileId,
                        input.Tiles[i].TileIndex,
                        SanitizeUri(input.Tiles[i].Uri),
                        envelope.EmbedderInput.Resolution,
                        envelope.EmbedderInput.TilesSizeMeters);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    telemetryState.SetOutcome(TelemetryOutcome.Retry, TelemetryErrorCategory.Publish);
                    WorkloadTelemetry.RecordTilePublishAttempt(
                        TelemetryOutcome.Failure,
                        overlay.RuleId,
                        input.Metadata.MissionMetadata.TenantId,
                        overlay.AreaOfInterest,
                        overlay.SensorName,
                        algorithmNameText);
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
                buildActivity.SetTag("findair.output.count", publishedCount);
            }
        }

        _logger.MessageProcessed(
            input.Tiles.Count,
            input.TilesAmount,
            mappedCoordinateCount,
            publishedCount);
        WorkloadTelemetry.RecordTileBatch(
            TelemetryOutcome.Success,
            overlay.RuleId,
            input.Metadata.MissionMetadata.TenantId,
            overlay.AreaOfInterest,
            overlay.SensorName,
            algorithmNameText);
        WorkloadTelemetry.RecordTiles(
            TelemetryOutcome.Success,
            overlay.RuleId,
            input.Metadata.MissionMetadata.TenantId,
            overlay.AreaOfInterest,
            overlay.SensorName,
            algorithmNameText,
            input.Metadata.ModelMetadata.TbCropSizeX,
            input.Metadata.ModelMetadata.TbCropSizeY,
            publishedCount);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(
                message.Headers,
                out var elapsedSeconds,
                _timeProvider))
        {
            PipelineTelemetry.RecordEndToEndDuration(PipelineStage.TbConsumer, elapsedSeconds);
        }

        return RabbitMqMessageProcessingResult.Success();
    }

    private static ValidationFailure? Validate(TbConsumerInputDto input)
    {
        if (string.IsNullOrWhiteSpace(input.RequestId))
        {
            return ValidationFailure.Create(
                "missing_request_id",
                "Validation failed: requestId is missing or empty.",
                input);
        }

        if (input.Metadata is null)
        {
            return ValidationFailure.Create(
                "missing_metadata",
                "Validation failed: metadata is missing.",
                input);
        }

        if (string.IsNullOrWhiteSpace(input.Metadata.TaskId))
        {
            return ValidationFailure.Create(
                "missing_task_id",
                "Validation failed: taskId is missing or empty.",
                input);
        }

        if (input.Metadata.MissionMetadata is null)
        {
            return ValidationFailure.Create(
                "missing_mission_metadata",
                "Validation failed: missionMetadata is missing.",
                input);
        }

        var mission = input.Metadata.MissionMetadata;
        if (mission.Overlay is null)
        {
            return ValidationFailure.Create(
                "missing_overlay",
                "Validation failed: overlay is missing.",
                input);
        }

        var overlay = mission.Overlay;
        if (string.IsNullOrWhiteSpace(mission.TenantId)
            || string.IsNullOrWhiteSpace(overlay.ImageId)
            || string.IsNullOrWhiteSpace(overlay.RuleId)
            || string.IsNullOrWhiteSpace(overlay.SensorName))
        {
            return ValidationFailure.Create(
                "missing_business_metadata",
                "Validation failed: tenantId, imageId, ruleId, and sensorName are required.",
                input);
        }

        if (input.Tiles is not { Count: > 0 })
        {
            return ValidationFailure.Create(
                "missing_tiles",
                "Validation failed: tileUniqueMetadata must contain at least one tile.",
                input);
        }

        if (input.TilesAmount <= 0 || input.TilesAmount < input.Tiles.Count)
        {
            return ValidationFailure.Create(
                "invalid_tiles_amount",
                "Validation failed: tilesAmount must be positive and not smaller than the batch tile count.",
                input);
        }

        if (input.BatchTilesAmount != input.Tiles.Count)
        {
            return ValidationFailure.Create(
                "batch_tiles_amount_mismatch",
                "Validation failed: batchTilesAmount must equal tileUniqueMetadata count.",
                input);
        }

        var indexes = new HashSet<int>();
        foreach (var tile in input.Tiles)
        {
            if (tile is null)
            {
                return ValidationFailure.Create(
                    "null_tile",
                    "Validation failed: tileUniqueMetadata contains a null tile.",
                    input);
            }

            if (tile.TileIndex < 0 || tile.TileIndex >= input.TilesAmount)
            {
                return ValidationFailure.Create(
                    "tile_index_out_of_range",
                    $"Validation failed: tile index {tile.TileIndex} must be between 0 and {input.TilesAmount - 1}.",
                    input,
                    tile.TileIndex);
            }

            if (!indexes.Add(tile.TileIndex))
            {
                return ValidationFailure.Create(
                    "duplicate_tile_index",
                    $"Validation failed: tile index {tile.TileIndex} is duplicated within the batch.",
                    input,
                    tile.TileIndex);
            }

            if (tile.Roi is not { Length: 4 }
                || tile.Roi.Any(value => !double.IsFinite(value))
                || string.IsNullOrWhiteSpace(tile.Uri))
            {
                return ValidationFailure.Create(
                    "invalid_tile_metadata",
                    "Validation failed: every tile requires a finite four-value ROI and non-empty URI.",
                    input,
                    tile.TileIndex);
            }
        }

        var matchedAlgorithms = overlay.AlgorithmNames;
        if (matchedAlgorithms is not { Count: > 0 }
            || matchedAlgorithms.Any(algorithm => !Enum.IsDefined(algorithm))
            || matchedAlgorithms.Distinct().Count() != matchedAlgorithms.Count)
        {
            return ValidationFailure.Create(
                "invalid_algorithms",
                $"Validation failed: algorithm_name must contain one or more unique algorithms. Valid algorithms are: {string.Join(", ", Enum.GetNames<AlgorithmName>())}",
                input);
        }

        return null;
    }

    private static TelemetryLogContext CreateTelemetryContext(TbConsumerInputDto input)
    {
        var metadata = input.Metadata;
        var mission = metadata?.MissionMetadata;
        var overlay = mission?.Overlay;
        return new TelemetryLogContext(
            TaskId: NullIfWhiteSpace(metadata?.TaskId),
            RequestId: NullIfWhiteSpace(input.RequestId),
            ImageId: NullIfWhiteSpace(overlay?.ImageId),
            RuleId: NullIfWhiteSpace(overlay?.RuleId),
            TenantId: NullIfWhiteSpace(mission?.TenantId),
            AlgorithmName: ValidAlgorithmNamesOrNull(overlay?.AlgorithmNames),
            AreaName: NullIfWhiteSpace(overlay?.AreaOfInterest),
            SensorName: NullIfWhiteSpace(overlay?.SensorName));
    }

    private static string? ValidAlgorithmNamesOrNull(IReadOnlyCollection<AlgorithmName>? algorithms) =>
        algorithms is { Count: > 0 } && algorithms.All(Enum.IsDefined)
            ? string.Join(",", algorithms)
            : null;

    private static string? NullIfWhiteSpace(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
    private static Activity? StartEmbedderBatchActivity(int tileCount)
    {
        var activity = TelemetrySources.TbConsumer.StartActivity(
            "embedder.publish_batch",
            ActivityKind.Producer);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "tb_consumer");
            activity.SetTag(TelemetryAttributeNames.TileCount, tileCount);
        }

        return activity;
    }

    private static string SanitizeUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return value;
        }

        return new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        }.Uri.AbsoluteUri;
    }

    private static Activity? StartStageActivity(string operation)
    {
        var spanName = operation switch
        {
            "projection" => "tb_consumer.projection",
            _ => "tb_consumer.stage"
        };
        var activity = TelemetrySources.TbConsumer.StartActivity(spanName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "tb_consumer");
            activity.SetTag("findair.operation", operation);
        }
        return activity;
    }

    private static bool IsRetry(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null)
        {
            return false;
        }

        foreach (var pair in headers)
        {
            if (!string.Equals(pair.Key, "retry-count", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var retryCount = pair.Value switch
            {
                int n => n,
                long n => (int)n,
                byte n => (int)n,
                short n => (int)n,
                _ => 0
            };

            return retryCount > 0;
        }

        return false;
    }

    private sealed record ValidationFailure(
        string Code,
        string Message,
        int? TilesAmount,
        int? BatchTilesAmount,
        int? ActualBatchTileCount,
        int? OffendingTileIndex)
    {
        public static ValidationFailure Create(
            string code,
            string message,
            TbConsumerInputDto input,
            int? offendingTileIndex = null) =>
            new(
                code,
                message,
                input.TilesAmount,
                input.BatchTilesAmount,
                input.Tiles?.Count,
                offendingTileIndex);
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
