using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
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
        using var stage = PipelineStageScope.Begin(PipelineStage.TbConsumer, message.Body.LongLength);

        try
        {
            if (!TryDeserialize(message, out var input, out var deserializationError))
            {
                stage.Rejected(TelemetryErrorCategory.Serialization);
                _logger.DeserializationRejected(deserializationError);
                return RabbitMqMessageProcessingResult.Failure(deserializationError);
            }

            // Built from unvalidated input, so every field is normalised defensively.
            var context = CreateTelemetryContext(input);
            using var pipelineScope = _logger.BeginTelemetryScope(context);
            Activity.Current.AddPipelineContext(context);

            var validationFailure = Validate(input);
            if (validationFailure is not null)
            {
                stage.Rejected(TelemetryErrorCategory.Validation);
                _logger.MessageRejected(
                    validationFailure.Code,
                    validationFailure.Message,
                    validationFailure.TilesAmount,
                    validationFailure.BatchTilesAmount,
                    validationFailure.ActualBatchTileCount,
                    validationFailure.OffendingTileIndex);
                return RabbitMqMessageProcessingResult.Failure(validationFailure.Message);
            }

            return await HandleValidatedMessageAsync(input, message, stage, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            stage.Cancelled();
            throw;
        }
        catch
        {
            stage.Faulted();
            throw;
        }
    }

    private async Task<RabbitMqMessageProcessingResult> HandleValidatedMessageAsync(
        TbConsumerInputDto input,
        RabbitMqMessageEnvelope message,
        PipelineStageScope stage,
        CancellationToken cancellationToken)
    {
        var overlay = input.Metadata.MissionMetadata.Overlay;
        var tenantId = input.Metadata.MissionMetadata.TenantId;
        var algorithmNameText = string.Join(",", overlay.AlgorithmNames.Select(algorithm => algorithm.ToString()));

        var context = new TelemetryLogContext(
            TaskId: input.Metadata.TaskId,
            RequestId: input.RequestId,
            ImageId: overlay.ImageId,
            RuleId: overlay.RuleId,
            TenantId: tenantId,
            AlgorithmName: algorithmNameText,
            AreaName: overlay.AreaOfInterest,
            SensorName: overlay.SensorName);
        var workload = new WorkloadDimensions(
            overlay.RuleId,
            tenantId,
            overlay.AreaOfInterest,
            overlay.SensorName,
            algorithmNameText);

        stage.RecordBatchSize(PipelineItem.Tile, input.Tiles.Count);

        var outgoingHeaders = FindAirMessageHeaders.Forward(message.Headers, algorithmNameText, tenantId);

        var projection = await MapTileCornersAsync(input, overlay, context, stage, cancellationToken);
        if (projection.Mapped is null)
        {
            return RabbitMqMessageProcessingResult.RetryableFailure(projection.FailureReason!);
        }

        var mappedCoordinateCount = projection.Mapped.Sum(coordinates => coordinates.Count / 2);
        stage.RecordBatchSize(PipelineItem.Coordinate, mappedCoordinateCount);

        // The batch span deliberately outlives the publish loop: the end-of-message summary log and
        // the workload counters below belong to it, so they stay visible on the batch in a trace.
        // The context is narrower than the projection span's — area and sensor are intentionally
        // omitted from the Embedder-facing producer span.
        using var batchSpan = PipelineSpanScope.StartProducer(
            PipelineStage.TbConsumer,
            "embedder.publish_batch",
            context with { AreaName = null, SensorName = null });
        batchSpan.SetTag(TelemetryAttributeNames.TileCount, input.Tiles.Count);

        await PublishToEmbedderAsync(
            input,
            projection.Mapped,
            context,
            workload,
            outgoingHeaders,
            batchSpan,
            stage,
            cancellationToken);

        _logger.MessageProcessed(
            input.Tiles.Count,
            input.TilesAmount,
            mappedCoordinateCount,
            stage.PublishedCount);
        WorkloadTelemetry.RecordTileBatch(TelemetryOutcome.Success, workload);
        WorkloadTelemetry.RecordTiles(
            TelemetryOutcome.Success,
            workload,
            input.Metadata.ModelMetadata.TbCropSizeX,
            input.Metadata.ModelMetadata.TbCropSizeY,
            stage.PublishedCount);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(
                message.Headers,
                out var elapsedSeconds,
                _timeProvider))
        {
            stage.RecordEndToEndDuration(elapsedSeconds);
        }

        stage.Succeeded();
        return RabbitMqMessageProcessingResult.Success();
    }

    /// <summary>
    /// Maps each tile's ROI to ground coordinates, four corners per tile. A count mismatch is
    /// reported as a failure reason rather than an exception because the batch is retryable.
    /// </summary>
    private async Task<ProjectionResult> MapTileCornersAsync(
        TbConsumerInputDto input,
        OverlayDto overlay,
        TelemetryLogContext context,
        PipelineStageScope stage,
        CancellationToken cancellationToken)
    {
        var tileCorners = input.Tiles
            .SelectMany(tile => new IReadOnlyList<double>[]
            {
                [tile.Roi[0], tile.Roi[1]], // top-left
                [tile.Roi[2], tile.Roi[1]], // top-right
                [tile.Roi[2], tile.Roi[3]], // bottom-right
                [tile.Roi[0], tile.Roi[3]], // bottom-left
            })
            .ToList();

        using var span = PipelineSpanScope.StartStage(PipelineStage.TbConsumer, "projection", context);
        span.SetTag(TelemetryAttributeNames.TileCount, input.Tiles.Count);

        try
        {
            var useRegistrationEndpoint =
                string.Equals(overlay.GridType, "MSP", StringComparison.Ordinal) &&
                overlay.ImageId.StartsWith("SHR", StringComparison.OrdinalIgnoreCase);

            var mapped = useRegistrationEndpoint
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

            if (mapped.Count != input.Tiles.Count * 4)
            {
                stage.Retryable(TelemetryErrorCategory.Dependency);
                span.Failed(TelemetryErrorCategory.Dependency);
                _logger.ProjectionResultCountMismatchScheduledForRetry(input.Tiles.Count, mapped.Count);
                return new ProjectionResult(
                    null,
                    $"Projection mapper returned {mapped.Count} results for {input.Tiles.Count} requested tiles.");
            }

            span.SetTag("findair.coordinate.count", mapped.Count);
            return new ProjectionResult(mapped, null);
        }
        catch (OperationCanceledException ex)
        {
            stage.Cancelled();
            span.Cancelled(ex);
            throw;
        }
        catch (Exception ex)
        {
            stage.Retryable(TelemetryErrorCategory.Dependency);
            span.Failed(TelemetryErrorCategory.Dependency, ex, recordException: false);
            // RabbitMQ's handler boundary owns the exception-bearing error log.
            _logger.ProjectionScheduledForRetry(input.Tiles.Count);
            throw;
        }
    }

    /// <summary>
    /// Publishes one Embedder input per tile. A failure part-way through leaves the already
    /// published outputs in place; the input is not acknowledged, so RabbitMQ redelivers the batch.
    /// </summary>
    /// <remarks>
    /// The batch span is owned by the caller, which keeps it open past this method so the
    /// end-of-message summary is attributed to it.
    /// </remarks>
    private async Task PublishToEmbedderAsync(
        TbConsumerInputDto input,
        IReadOnlyList<IReadOnlyList<double>> mappedCoordinates,
        TelemetryLogContext context,
        WorkloadDimensions workload,
        Dictionary<string, object?> outgoingHeaders,
        PipelineSpanScope span,
        PipelineStageScope stage,
        CancellationToken cancellationToken)
    {
        // A live view, so the trace context injected below is visible on every outgoing message.
        var headers = new ReadOnlyDictionary<string, object?>(outgoingHeaders);

        try
        {
            if (span.Activity is { } batchActivity)
            {
                _traceContextPropagator.Inject(outgoingHeaders, batchActivity.Context);
            }
            else
            {
                _traceContextPropagator.InjectCurrent(outgoingHeaders);
            }

            var envelopes = _embedderInputMessageBuilder.Build(input, mappedCoordinates, _timeProvider.GetUtcNow());
            for (var i = 0; i < envelopes.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var envelope = envelopes[i];
                var body = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
                stage.RecordEgressPayloadSize(body.LongLength);
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

                    stage.MessagePublished();
                    WorkloadTelemetry.RecordTilePublishAttempt(TelemetryOutcome.Success, workload);
                    LogTileSentToEmbedder(context, input.Tiles[i], envelope);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    stage.Retryable(TelemetryErrorCategory.Publish);
                    WorkloadTelemetry.RecordTilePublishAttempt(TelemetryOutcome.Failure, workload);
                    span.Failed(TelemetryErrorCategory.Publish, ex, recordException: false);
                    // RabbitMQ's handler boundary owns the exception-bearing error log.
                    _logger.OutputPublishScheduledForRetry(stage.PublishedCount, input.Tiles.Count);
                    throw;
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            stage.Cancelled();
            span.Cancelled(ex);
            throw;
        }
        catch (Exception ex)
        {
            if (stage.Faulted())
            {
                span.Failed(TelemetryErrorCategory.Handler, ex);
            }

            throw;
        }
        finally
        {
            span.SetTag("findair.output.count", stage.PublishedCount);
        }
    }

    private void LogTileSentToEmbedder(
        in TelemetryLogContext context,
        TileBuilderTileOutput tile,
        EmbedderInputDto envelope)
    {
        using var tileScope = _logger.BeginTelemetryScope(context with
        {
            TileId = envelope.EmbedderInput.TileId,
            TileIndex = tile.TileIndex
        });
        _logger.TileSentToEmbedder(
            envelope.EmbedderInput.TileId,
            tile.TileIndex,
            SanitizeUri(tile.Uri),
            envelope.EmbedderInput.Resolution,
            envelope.EmbedderInput.TilesSizeMeters);
    }

    private static bool TryDeserialize(
        RabbitMqMessageEnvelope message,
        [NotNullWhen(true)] out TbConsumerInputDto? input,
        [NotNullWhen(false)] out string? error)
    {
        try
        {
            input = JsonSerializer.Deserialize<TbConsumerInputDto>(message.BodyAsUtf8(), SerializerOptions);
            error = input is null ? "Deserialization produced null." : null;
            return input is not null;
        }
        catch (JsonException ex)
        {
            input = null;
            error = $"Json deserialization failed: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            input = null;
            error = $"Unhandled deserialization error: {ex.Message}";
            return false;
        }
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

    private readonly record struct ProjectionResult(
        IReadOnlyList<IReadOnlyList<double>>? Mapped,
        string? FailureReason);

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
}
