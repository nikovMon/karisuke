using System.Text.Json;
using System.Diagnostics;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Observability;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Errors;
using ImagingPipeline.TbPublisher.Processing;
using Microsoft.Extensions.Logging;
using OpenTelemetry;

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

        PipelineTelemetry.RecordPayloadSize(PipelineStage.TbPublisher, PipelineDirection.Ingress, message.Body.LongLength);
        if (PipelineTimingHeaders.TryGetElapsedSeconds(message.Headers, out var elapsedSeconds))
        {
            PipelineTelemetry.RecordEndToEndDuration(PipelineStage.TbPublisher, elapsedSeconds);
        }

        try
        {
            GatewayOutputMessageDto inputMessage;
            string algorithmNameText;
            using (var validationActivity = StartStageActivity("validate"))
            {
                try
                {
                    var validation = _validator.Validate(message.Body);
                    if (!validation.IsValid)
                    {
                        var validationError = string.Join(" | ", validation.Errors);
                        outcome = TelemetryOutcome.Rejected;
                        error = TelemetryErrorCategory.Validation;
                        validationActivity.SetTelemetryError(error);
                        if (validationActivity?.IsAllDataRequested == true)
                        {
                            validationActivity.SetTag(
                                "imaging_pipeline.validation.error.count",
                                validation.Errors.Count);
                        }
                        _logger.MessageRejected(validationError);
                        return RabbitMqMessageProcessingResult.Failure(validationError);
                    }

                    inputMessage = validation.Message!;
                    algorithmNameText = string.Join(",", inputMessage.AlgorithmNames);
                    validationActivity
                        .AddPipelineContext(
                            taskId: inputMessage.TaskId,
                            imageId: inputMessage.ImageId,
                            ruleId: inputMessage.RuleId,
                            tenantId: inputMessage.TenantId,
                            algorithmName: algorithmNameText)
                        .SetTelemetrySuccess();
                }
                catch (OperationCanceledException ex)
                {
                    validationActivity.SetTelemetryError(
                        TelemetryErrorCategory.Cancelled,
                        ex,
                        recordException: false);
                    throw;
                }
                catch (Exception ex)
                {
                    validationActivity.SetTelemetryError(TelemetryErrorCategory.Handler, ex);
                    throw;
                }
            }

            var requestId = Baggage.Current.GetBaggage(TelemetryAttributeNames.PipelineRequestId);
            Activity.Current.AddPipelineContext(
                taskId: inputMessage.TaskId,
                requestId: requestId,
                imageId: inputMessage.ImageId,
                ruleId: inputMessage.RuleId,
                tenantId: inputMessage.TenantId,
                algorithmName: algorithmNameText);

            using var pipelineScope = _logger.BeginTelemetryScope(new TelemetryLogContext(
                TaskId: inputMessage.TaskId,
                RequestId: requestId,
                ImageId: inputMessage.ImageId,
                RuleId: inputMessage.RuleId,
                TenantId: inputMessage.TenantId,
                AlgorithmName: algorithmNameText));
            using var correlationBaggage = PipelineCorrelationBaggage.Push(
                new PipelineCorrelationContext(
                    TaskId: inputMessage.TaskId,
                    ImageId: inputMessage.ImageId,
                    RuleId: inputMessage.RuleId,
                    TenantId: inputMessage.TenantId,
                    AlgorithmName: algorithmNameText),
                includeExistingCanonicalValues: true);

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
                            "imaging_pipeline.pipeline.ground_point.count",
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
                            "imaging_pipeline.pipeline.coordinate.count",
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
                    return RabbitMqMessageProcessingResult.Failure($"projection mapping failed: {ex.Message}");
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

            using var buildActivity = StartStageActivity("build");
            try
            {
                buildActivity.AddPipelineContext(
                    taskId: inputMessage.TaskId,
                    imageId: inputMessage.ImageId,
                    ruleId: inputMessage.RuleId,
                    tenantId: inputMessage.TenantId,
                    algorithmName: algorithmNameText);
                var outputMessages = _outputMessageBuilder.Map(inputMessage, focusedPxWkt);
                outputCount = outputMessages.Count;
                if (buildActivity?.IsAllDataRequested == true)
                {
                    buildActivity.SetTag("imaging_pipeline.pipeline.output.count", outputCount);
                }

                var index = 0;
                foreach (var ingestMessage in outputMessages)
                {
                    var outputBody = JsonSerializer.SerializeToUtf8Bytes(ingestMessage, SerializerOptions);
                    PipelineTelemetry.RecordPayloadSize(
                        PipelineStage.TbPublisher,
                        PipelineDirection.Egress,
                        outputBody.LongLength);

                    var outputEnvelope = new RabbitMqMessageEnvelope(
                        $"{message.MessageId}-{index}",
                        outputBody,
                        Headers: message.Headers is null
                            ? null
                            : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal),
                        CorrelationId: message.CorrelationId ?? message.MessageId);
                    using var outputLogScope = _logger.BeginTelemetryScope(
                        new TelemetryLogContext(RequestId: ingestMessage.RequestId));
                    using var outputCorrelationBaggage = PipelineCorrelationBaggage.Push(
                        new PipelineCorrelationContext(
                            TaskId: inputMessage.TaskId,
                            RequestId: ingestMessage.RequestId,
                            ImageId: inputMessage.ImageId,
                            RuleId: inputMessage.RuleId,
                            TenantId: inputMessage.TenantId,
                            AlgorithmName: algorithmNameText));
                    try
                    {
                        await _publisher.PublishToOutputAsync(outputEnvelope, cancellationToken);
                        publishedCount++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        outcome = TelemetryOutcome.Retry;
                        error = TelemetryErrorCategory.Publish;
                        // RabbitMQ's handler boundary owns the exception-bearing error log.
                        _logger.OutputPublishScheduledForRetry(publishedCount, outputCount);
                        throw;
                    }

                    index++;
                }

                buildActivity.SetTelemetrySuccess();
            }
            catch (OperationCanceledException ex)
            {
                buildActivity.SetTelemetryError(
                    TelemetryErrorCategory.Cancelled,
                    ex,
                    recordException: false);
                throw;
            }
            catch (Exception ex)
            {
                if (error != TelemetryErrorCategory.Publish)
                {
                    outcome = TelemetryOutcome.Retry;
                    error = TelemetryErrorCategory.Serialization;
                }

                buildActivity.SetTelemetryError(
                    error,
                    ex,
                    recordException: error != TelemetryErrorCategory.Publish);
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
            _logger.MessageProcessed(roiCoordinates.Count, inputMessage.TilingConfigs.Count, outputCount);
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
            "validate" => "tb_publisher.validate",
            "geometry" => "tb_publisher.geometry",
            "projection" => "tb_publisher.projection",
            "build" => "tb_publisher.build",
            _ => "tb_publisher.stage"
        };
        var activity = TelemetrySources.TbPublisher.StartActivity(spanName, ActivityKind.Internal);
        if (activity?.IsAllDataRequested == true)
        {
            activity.SetTag(TelemetryAttributeNames.PipelineStage, "tb_publisher");
            activity.SetTag("imaging_pipeline.pipeline.operation", operation);
        }
        return activity;
    }
}
