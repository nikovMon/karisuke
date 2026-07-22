using ImagingPipeline.Observability;
using System.Security.Cryptography;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal sealed record RabbitMqDelivery(
    ulong DeliveryTag,
    RabbitMqMessageEnvelope Message,
    bool Redelivered,
    double? PublishedToDeliverySeconds);

internal static class RabbitMqDeliveryFactory
{
    public static RabbitMqDelivery Create(BasicDeliverEventArgs args)
    {
        var properties = args.BasicProperties;
        var headers = RabbitMqHeaders.Clone(properties.Headers);
        // Messages entering through a producer outside this library receive a stable,
        // canonical end-to-end clock at the first ImagingPipeline boundary.
        PipelineTimingHeaders.EnsureStarted(headers);

        var hasDeliveryDelay = MessagingTimingHeaders.TryGetDeliveryDelaySeconds(
            headers,
            out var deliveryDelaySeconds);
        var message = new RabbitMqMessageEnvelope(
            ReadStableMessageId(properties.MessageId, args.Body),
            args.Body.ToArray(),
            properties.ContentType ?? "application/octet-stream",
            headers,
            properties.CorrelationId);
        return new RabbitMqDelivery(
            args.DeliveryTag,
            message,
            args.Redelivered,
            hasDeliveryDelay ? deliveryDelaySeconds : null);
    }

    private static string ReadStableMessageId(string? messageId, ReadOnlyMemory<byte> body)
    {
        if (!string.IsNullOrWhiteSpace(messageId))
        {
            return messageId;
        }

        return $"body-sha256:{Convert.ToHexString(SHA256.HashData(body.Span))}";
    }
}
