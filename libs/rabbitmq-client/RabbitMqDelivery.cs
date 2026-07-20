using System.Security.Cryptography;
using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitMqClient;

internal sealed record RabbitMqDelivery(ulong DeliveryTag, RabbitMqMessageEnvelope Message);

internal static class RabbitMqDeliveryFactory
{
    public static RabbitMqDelivery Create(BasicDeliverEventArgs args)
    {
        var properties = args.BasicProperties;
        var headers = properties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(properties.Headers, StringComparer.Ordinal);
        var message = new RabbitMqMessageEnvelope(
            ReadStableMessageId(properties.MessageId, args.Body),
            args.Body.ToArray(),
            properties.ContentType ?? "application/octet-stream",
            headers,
            properties.CorrelationId);
        return new RabbitMqDelivery(args.DeliveryTag, message);
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
