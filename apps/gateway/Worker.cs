namespace ImagingPipeline.Gateway;

using System.Text.Json;
using ImagingPipeline.Gateway.Application.Messages;
using ImagingPipeline.Gateway.Application.RabbitMq;
using ImagingPipeline.Gateway.Application.Rules;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Domain;
using ImagingPipeline.Gateway.Dtos.Messages;
using Microsoft.Extensions.Options;

public sealed class Worker : BackgroundService
{
    private readonly RabbitMqGatewayConsumer _consumer;
    private readonly RabbitMqGatewayPublisher _publisher;
    private readonly RuleCache _ruleCache;
    private readonly InputMessageValidator _inputValidator;
    private readonly RuleMatcher _ruleMatcher;
    private readonly JsonOutputBuilder _outputBuilder;
    private readonly JsonPathReader _pathReader;
    private readonly GatewaySettings _gatewaySettings;
    private readonly InputFieldPathSettings _inputPaths;

    public Worker(
        RabbitMqGatewayConsumer consumer,
        RabbitMqGatewayPublisher publisher,
        RuleCache ruleCache,
        InputMessageValidator inputValidator,
        RuleMatcher ruleMatcher,
        JsonOutputBuilder outputBuilder,
        JsonPathReader pathReader,
        IOptions<GatewaySettings> gatewaySettings,
        IOptions<InputFieldPathSettings> inputPaths)
    {
        _consumer = consumer;
        _publisher = publisher;
        _ruleCache = ruleCache;
        _inputValidator = inputValidator;
        _ruleMatcher = ruleMatcher;
        _outputBuilder = outputBuilder;
        _pathReader = pathReader;
        _gatewaySettings = gatewaySettings.Value;
        _inputPaths = inputPaths.Value;
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        _consumer.ConsumeAsync(ProcessDeliveryAsync, stoppingToken);

    private async Task ProcessDeliveryAsync(
        RabbitMqGatewayDelivery delivery,
        CancellationToken cancellationToken)
    {
        try
        {
            var rules = _ruleCache.Current;
            var input = _inputValidator.Validate(delivery.Body);
            var matches = _ruleMatcher.Match(input, rules);
            var outputs = _outputBuilder.BuildOutputs(input, matches);
            var correlationId = delivery.CorrelationId ?? delivery.MessageId;

            foreach (var output in outputs)
            {
                await _publisher.PublishOutputAsync(output, correlationId, cancellationToken);
            }

            await delivery.AckAsync(cancellationToken);
        }
        catch (NonRetryableGatewayException ex)
        {
            var failure = CreateFailureMessage(delivery, ex);
            await _publisher.PublishFailureAsync(
                failure,
                delivery.CorrelationId ?? delivery.MessageId,
                cancellationToken);
            await delivery.AckAsync(cancellationToken);
        }
    }

    private GatewayFailureMessage CreateFailureMessage(
        RabbitMqGatewayDelivery delivery,
        NonRetryableGatewayException exception)
    {
        var originalPayload = ReadOriginalPayload(delivery.Body);
        return new GatewayFailureMessage
        {
            Service = _gatewaySettings.ServiceName,
            Stage = "validation",
            ErrorCode = exception.ErrorCode,
            ErrorMessage = exception.Message,
            OriginalMessageId = delivery.MessageId,
            ImageId = TryReadImageId(originalPayload),
            OriginalRoutingKey = delivery.OriginalRoutingKey,
            OriginalPayload = originalPayload
        };
    }

    private static JsonElement ReadOriginalPayload(ReadOnlyMemory<byte> body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return JsonSerializer.SerializeToElement(new
            {
                rawBodyBase64 = Convert.ToBase64String(body.Span)
            });
        }
    }

    private string? TryReadImageId(JsonElement payload)
    {
        return payload.ValueKind == JsonValueKind.Object &&
            _pathReader.TryReadNonEmptyString(payload, _inputPaths.ImageIdPath, out var imageId)
                ? imageId
                : null;
    }
}
