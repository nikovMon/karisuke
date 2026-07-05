using System.Text.Json;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.ProjectionMapperClient;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.TbPublisher.Observability;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.TbPublisher.Application;

public sealed class TbPublisherMessageHandler : IRabbitMqMessageHandler
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly ITbMessageValidator _validator;
    private readonly IProjectionMapperClient _projectionMapperClient;
    private readonly ITilingConfigMapper _tilingConfigMapper;
    private readonly IRabbitMqPublisher _publisher;
    private readonly ILogger<TbPublisherMessageHandler> _logger;

    public TbPublisherMessageHandler(
        ITbMessageValidator validator,
        IProjectionMapperClient projectionMapperClient,
        ITilingConfigMapper tilingConfigMapper,
        IRabbitMqPublisher publisher,
        ILogger<TbPublisherMessageHandler> logger)
    {
        _validator = validator;
        _projectionMapperClient = projectionMapperClient;
        _tilingConfigMapper = tilingConfigMapper;
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
        var tbMessage = validation.Message!;

        IReadOnlyList<IReadOnlyList<double>> groundPoints;
        try
        {
            var roiGeometry = GeometryUtilities.ReadGeoJson(tbMessage.RoiFootprint);
            groundPoints = roiGeometry.Coordinates
                .Select(coordinate => (IReadOnlyList<double>)[coordinate.X, coordinate.Y])
                .ToList();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                ex,
                "TBPublisher message {MessageId} has an invalid roiFootprint for image {ImageId}.",
                message.MessageId,
                tbMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure($"roiFootprint is invalid: {ex.Message}");
        }

        string focusedPxWkt;
        try
        {
            var request = new ProjectionMapperRequestDto { GroundPoints = groundPoints };
            var coordinates = await _projectionMapperClient.MapAsync(tbMessage.ImageId, request, cancellationToken);
            var focusedGeometry = GeometryUtilities.CreatePolygonFromCoordinates(coordinates);
            focusedPxWkt = GeometryUtilities.WriteWkt(focusedGeometry);
        }
        catch (ProjectionMapperClientException ex)
        {
            _logger.LogError(
                ex,
                "TBPublisher message {MessageId} failed projection mapping for image {ImageId}.",
                message.MessageId,
                tbMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure($"projection mapping failed: {ex.Message}");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(
                ex,
                "TBPublisher message {MessageId} received an invalid pixel geometry from the projection mapper for image {ImageId}.",
                message.MessageId,
                tbMessage.ImageId);
            return RabbitMqMessageProcessingResult.Failure($"projected pixel geometry is invalid: {ex.Message}");
        }

        var missionId = Guid.NewGuid().ToString();
        var mapping = _tilingConfigMapper.Map(tbMessage, focusedPxWkt, missionId);
        if (!mapping.IsSuccess)
        {
            TbPublisherDiagnostics.TilingConfigMappingFailures.Add(1);
            _logger.LogWarning(
                "TBPublisher message {MessageId} failed tiling-config mapping. Error: {Error}",
                message.MessageId,
                mapping.Error);
            return RabbitMqMessageProcessingResult.Failure(mapping.Error ?? "tiling-config mapping failed.");
        }

        var index = 0;
        foreach (var ingestMessage in mapping.Messages!)
        {
            var outputBody = JsonSerializer.SerializeToUtf8Bytes(ingestMessage, SerializerOptions);
            var outputEnvelope = new RabbitMqMessageEnvelope($"{message.MessageId}-{index}", outputBody);
            await _publisher.PublishToOutputAsync(outputEnvelope, cancellationToken);
            index++;
        }

        TbPublisherDiagnostics.MessagesPublishedToTilingConfig.Add(mapping.Messages!.Count);
        _logger.LogInformation(
            "TBPublisher message {MessageId} processed successfully for tenant {TenantId}, published {Count} tiling-config message(s).",
            message.MessageId,
            tbMessage.TenantId,
            mapping.Messages!.Count);
        return new RabbitMqMessageProcessingResult(true, null, null);
    }
}
