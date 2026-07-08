using System.Text.Json;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Errors;
using ImagingPipeline.TbPublisher.Observability;
using ImagingPipeline.TbPublisher.Processing;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbPublisher.Application;

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
        using var activity = TbPublisherDiagnostics.ActivitySource.StartActivity("tb-publisher handle message");
        activity?.SetTag("messaging.message.id", message.MessageId);

        var validation = _validator.Validate(message.Body);
        if (!validation.IsValid)
        {
            TbPublisherDiagnostics.ValidationFailures.Add(1);
            var validationError = string.Join(" | ", validation.Errors);
            _logger.LogWarning(
                "TBPublisher message {MessageId} failed validation. Errors: {ValidationErrors}",
                message.MessageId,
                validationError);
            return RabbitMqMessageProcessingResult.Failure(validationError);
        }

        TbPublisherDiagnostics.MessagesValidated.Add(1);
        var inputMessage = validation.Message!;

        IReadOnlyList<IReadOnlyList<double>> groundPoints;
        try
        {
            groundPoints = _geometryConverter.ExtractGroundPoints(inputMessage.RoiFootprint);
        }
        catch (TbPublisherValidationException ex)
        {
            _logger.LogWarning(
                ex,
                "TBPublisher message {MessageId} has an invalid roiFootprint for image {ImageId}.",
                message.MessageId,
                inputMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure(ex.Message);
        }

        string focusedPxWkt;
        try
        {
            var request = new ProjectionMapperRequestDto { GroundPoints = groundPoints };
            var coordinates = await _projectionMapperClient.MapAsync(inputMessage.ImageId, request, cancellationToken);
            focusedPxWkt = _geometryConverter.BuildFocusedPxWkt(coordinates);
        }
        catch (ProjectionMapperClientException ex)
        {
            _logger.LogError(
                ex,
                "TBPublisher message {MessageId} failed projection mapping for image {ImageId}.",
                message.MessageId,
                inputMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure($"projection mapping failed: {ex.Message}");
        }
        catch (TbPublisherValidationException ex)
        {
            _logger.LogError(
                ex,
                "TBPublisher message {MessageId} received an invalid pixel geometry from the projection mapper for image {ImageId}.",
                message.MessageId,
                inputMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure(ex.Message);
        }

        var outputMessages = _outputMessageBuilder.Map(inputMessage, focusedPxWkt);

        var index = 0;
        try
        {
            foreach (var ingestMessage in outputMessages)
            {
                var outputBody = JsonSerializer.SerializeToUtf8Bytes(ingestMessage, SerializerOptions);
                var outputEnvelope = new RabbitMqMessageEnvelope($"{message.MessageId}-{index}", outputBody);
                await _publisher.PublishToOutputAsync(outputEnvelope, cancellationToken);
                index++;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "TBPublisher message {MessageId} failed while publishing output messages: {PublishedCount} of {TotalCount} were already published before the failure and may be duplicated if this message is later replayed from the dead-letter queue.",
                message.MessageId,
                index,
                outputMessages.Count);
            throw;
        }

        TbPublisherDiagnostics.MessagesPublishedToOutput.Add(outputMessages.Count);
        _logger.LogInformation(
            "TBPublisher message {MessageId} processed successfully for tenant {TenantId}, published {Count} tiling-config message(s).",
            message.MessageId,
            inputMessage.TenantId,
            outputMessages.Count);
        return new RabbitMqMessageProcessingResult(true, null, null);
    }
}
