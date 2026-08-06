using System.Text.Json;
using System.Diagnostics;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Errors;
using ImagingPipeline.TbPublisher.Processing;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbPublisher.MessageHandling;

public sealed class TbPublisherMessageHandler : IRabbitMqMessageHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IInputMessageValidator _validator;
    private readonly TbPublisherGeometryConverter _geometryConverter;
    private readonly IProjectionMapperClient _projectionMapperClient;
    private readonly ITbPublisherOutputMessageBuilder _outputMessageBuilder;
    private readonly IRabbitMqPublisher _publisher;
    private readonly ILogger<TbPublisherMessageHandler> _logger;

    public TbPublisherMessageHandler(
        IInputMessageValidator validator,
        TbPublisherGeometryConverter geometryConverter,
        IProjectionMapperClient projectionMapperClient,
        ITbPublisherOutputMessageBuilder outputMessageBuilder,
        IRabbitMqPublisher publisher,
        ILogger<TbPublisherMessageHandler> logger)
    {
        _validator = validator;
        _geometryConverter = geometryConverter;
        _projectionMapperClient = projectionMapperClient;
        _outputMessageBuilder = outputMessageBuilder;
        _publisher = publisher;
        _logger = logger;
    }

    public async Task<RabbitMqMessageProcessingResult> HandleAsync(
        RabbitMqMessageEnvelope message,
        CancellationToken cancellationToken = default)
    {
        var started = TelemetryTiming.StartTimestamp();
        var outcome = TelemetryOutcome.Failure;
        var error = TelemetryErrorCategory.Unknown;
        var outputCount = 0;
        var publishedCount = 0;
        var requestIds = string.Empty;

        PipelineTelemetry.RecordPayloadSize(PipelineStage.TbPublisher, PipelineDirection.Ingress, message.Body.LongLength);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(message.Headers, out var elapsedSeconds))
        {
            PipelineTelemetry.RecordEndToEndDuration(PipelineStage.TbPublisher, elapsedSeconds);
        }

        try
        {
            var validation = _validator.Validate(message.Body);
            if (!validation.IsValid)
            {
                var validationError = string.Join(" | ", validation.Errors);
                outcome = TelemetryOutcome.Rejected;
                error = TelemetryErrorCategory.Validation;
                _logger.MessageRejected(validationError);
                return RabbitMqMessageProcessingResult.Failure(validationError);
            }

            var inputMessage = validation.Message!;
            var algorithmNameText = string.Join(",", inputMessage.AlgorithmNames);
            Activity.Current.AddPipelineContext(
                taskId: inputMessage.TaskId,
                imageId: inputMessage.ImageId,
                ruleId: inputMessage.RuleId,
                tenantId: inputMessage.TenantId,
                algorithmName: algorithmNameText,
                areaName: inputMessage.AreaOfInterest,
                sensorName: inputMessage.SensorName);

            using var pipelineScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
                TaskId: inputMessage.TaskId,
                ImageId: inputMessage.ImageId,
                RuleId: inputMessage.RuleId,
                TenantId: inputMessage.TenantId,
                AlgorithmName: algorithmNameText,
                AreaName: inputMessage.AreaOfInterest,
                SensorName: inputMessage.SensorName));

            PipelineTelemetry.RecordBatchSize(
                PipelineStage.TbPublisher,
                PipelineItem.TilingConfig,
                inputMessage.TilingConfigs.Count);

            IReadOnlyList<IReadOnlyList<double>> roiCoordinates;
            using (var geometryActivity = StartStageActivity("geometry"))
            {
                try
                {
                    geometryActivity.AddPipelineContext(
                        taskId: inputMessage.TaskId,
                        imageId: inputMessage.ImageId,
                        ruleId: inputMessage.RuleId,
                        tenantId: inputMessage.TenantId,
                        algorithmName: algorithmNameText);
                    roiCoordinates = _geometryConverter.ExtractCoordinates(inputMessage.RoiFootprint);
                    if (geometryActivity?.IsAllDataRequested == true)
                    {
                        geometryActivity.SetTag(
                            "findair.ground_point.count",
                            roiCoordinates.Count);
                    }
                    geometryActivity.SetTelemetrySuccess();
                }
                catch (TbPublisherValidationException ex)
                {
                    outcome = TelemetryOutcome.Rejected;
                    error = TelemetryErrorCategory.Validation;
                    geometryActivity.SetTelemetryError(error, ex);
                    _logger.InvalidRoiGeometry(ex);
                    return RabbitMqMessageProcessingResult.Failure(ex.Message);
                }
                catch (OperationCanceledException ex)
                {
                    geometryActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
                catch (Exception ex)
                {
                    geometryActivity.SetTelemetryError(TelemetryErrorCategory.Handler, ex);
                    throw;
                }
            }

            PipelineTelemetry.RecordBatchSize(PipelineStage.TbPublisher, PipelineItem.GroundPoint, roiCoordinates.Count);

            string focusedPxWkt;
            IReadOnlyList<IReadOnlyList<double>> coordinates;
            using (var projectionActivity = StartStageActivity("projection"))
            {
                try
                {
                    projectionActivity.AddPipelineContext(
                        taskId: inputMessage.TaskId,
                        imageId: inputMessage.ImageId,
                        ruleId: inputMessage.RuleId,
                        tenantId: inputMessage.TenantId,
                        algorithmName: algorithmNameText);
                    var request = new ProjectionMapperRequestDto { Coordinates = roiCoordinates };
                    coordinates = await _projectionMapperClient.MapAsync(inputMessage.ImageId, request, cancellationToken);
                    if (projectionActivity?.IsAllDataRequested == true)
                    {
                        projectionActivity.SetTag(
                            "findair.coordinate.count",
                            coordinates.Count);
                    }
                    focusedPxWkt = _geometryConverter.BuildFocusedPxWkt(coordinates);
                    projectionActivity.SetTelemetrySuccess();
                }
                catch (ProjectionMapperClientException ex)
                {
                    outcome = TelemetryOutcome.Failure;
                    error = TelemetryErrorCategory.Dependency;
                    projectionActivity.SetTelemetryError(error, ex, recordException: false);
                    _logger.ProjectionFailed(ex);
                    return RabbitMqMessageProcessingResult.RetryableFailure($"projection mapping failed: {ex.Message}");
                }
                catch (TbPublisherValidationException ex)
                {
                    outcome = TelemetryOutcome.Rejected;
                    error = TelemetryErrorCategory.Validation;
                    projectionActivity.SetTelemetryError(error, ex);
                    _logger.InvalidProjectedGeometry(ex);
                    return RabbitMqMessageProcessingResult.Failure(ex.Message);
                }
                catch (OperationCanceledException ex)
                {
                    projectionActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
                catch (Exception ex)
                {
                    outcome = TelemetryOutcome.Retry;
                    error = TelemetryErrorCategory.Dependency;
                    projectionActivity.SetTelemetryError(error, ex);
                    throw;
                }
            }

            PipelineTelemetry.RecordBatchSize(PipelineStage.TbPublisher, PipelineItem.Coordinate, coordinates.Count);

            try
            {
                var outputMessages = _outputMessageBuilder.Map(inputMessage, focusedPxWkt);
                outputCount = outputMessages.Count;
                requestIds = string.Join(",", outputMessages.Select(output => output.RequestId));

                var index = 0;
                foreach (var ingestMessage in outputMessages)
                {
                    var outputBody = JsonSerializer.SerializeToUtf8Bytes(
                        ingestMessage,
                        SerializerOptions);
                    PipelineTelemetry.RecordPayloadSize(
                        PipelineStage.TbPublisher,
                        PipelineDirection.Egress,
                        outputBody.LongLength);

                    var outputHeaders = FindAirMessageHeaders.Forward(
                        message.Headers,
                        algorithmNameText);
                    var outputEnvelope = new RabbitMqMessageEnvelope(
                        $"{message.MessageId}-{index}",
                        outputBody,
                        Headers: outputHeaders);
                    using var outputLogScope = _logger.BeginTelemetryScope(
                        new TelemetryLogContext(RequestId: ingestMessage.RequestId));
                    try
                    {
                        await _publisher.PublishToOutputAsync(
                            outputEnvelope,
                            cancellationToken);
                        publishedCount++;
                    }
                    catch (Exception) when (!cancellationToken.IsCancellationRequested)
                    {
                        outcome = TelemetryOutcome.Retry;
                        error = TelemetryErrorCategory.Publish;
                        _logger.OutputPublishScheduledForRetry(
                            publishedCount,
                            outputCount);
                        throw;
                    }

                    index++;
                }
            }
            catch (OperationCanceledException)
            {
                outcome = TelemetryOutcome.Cancelled;
                error = TelemetryErrorCategory.Cancelled;
                throw;
            }
            catch
            {
                if (error != TelemetryErrorCategory.Publish)
                {
                    outcome = TelemetryOutcome.Retry;
                    error = TelemetryErrorCategory.Serialization;
                }

                throw;
            }
            outcome = TelemetryOutcome.Success;
            error = TelemetryErrorCategory.None;
            PipelineTelemetry.RecordFanOut(PipelineStage.TbPublisher, outputCount);
            PipelineTelemetry.RecordMessage(
                PipelineStage.TbPublisher,
                PipelineDirection.Egress,
                TelemetryOutcome.Success,
                count: publishedCount);
            _logger.MessageProcessed(
                roiCoordinates.Count,
                inputMessage.TilingConfigs.Count,
                outputCount,
                requestIds);
            WorkloadTelemetry.RecordTask(
                PipelineDirection.Ingress,
                TelemetryOutcome.Success,
                inputMessage.RuleId,
                inputMessage.TenantId,
                inputMessage.AreaOfInterest,
                inputMessage.SensorName,
                algorithmNameText);
            WorkloadTelemetry.RecordTileRequest(
                TelemetryOutcome.Success,
                inputMessage.RuleId,
                inputMessage.TenantId,
                inputMessage.AreaOfInterest,
                inputMessage.SensorName,
                algorithmNameText,
                publishedCount);
            return new RabbitMqMessageProcessingResult(true, null, null);
        }
        catch (OperationCanceledException)
        {
            outcome = TelemetryOutcome.Cancelled;
            error = TelemetryErrorCategory.Cancelled;
            throw;
        }
        catch
        {
            if (outcome == TelemetryOutcome.Failure && error == TelemetryErrorCategory.Unknown)
            {
                outcome = TelemetryOutcome.Retry;
                error = TelemetryErrorCategory.Handler;
            }

            throw;
        }
        finally
        {
            if (publishedCount > 0 && outcome != TelemetryOutcome.Success)
            {
                PipelineTelemetry.RecordMessage(
                    PipelineStage.TbPublisher,
                    PipelineDirection.Egress,
                    TelemetryOutcome.Success,
                    count: publishedCount);
            }

            PipelineTelemetry.RecordMessage(PipelineStage.TbPublisher, PipelineDirection.Ingress, outcome, error);
            PipelineTelemetry.RecordStageDuration(
                PipelineStage.TbPublisher,
                TelemetryTiming.ElapsedSeconds(started),
                outcome,
                error);
        }
    }

    private static Activity? StartStageActivity(string operation)
    {
        var spanName = operation switch
        {
            "geometry" => "tb_publisher.geometry",
            "projection" => "tb_publisher.projection",
            _ => "tb_publisher.stage"
        };
        var activity = TelemetrySources.TbPublisher.StartActivity(spanName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "tb_publisher");
            activity.SetTag("findair.operation", operation);
        }
        return activity;
    }
}
