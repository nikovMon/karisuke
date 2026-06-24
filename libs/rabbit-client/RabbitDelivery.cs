using RabbitMQ.Client.Events;

namespace ImagingPipeline.RabbitClient;

internal sealed record RabbitDelivery(ulong DeliveryTag, RabbitMessageEnvelope Message);

internal static class RabbitDeliveryFactory
{
    public static RabbitDelivery Create(BasicDeliverEventArgs args)
    {
        var properties = args.BasicProperties;
        var headers = properties.Headers is null
            ? new Dictionary<string, object?>()
            : new Dictionary<string, object?>(properties.Headers, StringComparer.Ordinal);
        var message = new RabbitMessageEnvelope(
            properties.MessageId ?? Guid.NewGuid().ToString("N"),
            args.Body.ToArray(),
            properties.ContentType ?? "application/octet-stream",
            headers,
            properties.CorrelationId);
        return new RabbitDelivery(args.DeliveryTag, message);
    }

    public static int RetryCount(RabbitMessageEnvelope message)
    {
        if (message.Headers is null || !message.Headers.TryGetValue("x-imagingpipeline-retry-count", out var value)) return 0;
        return value switch
        {
            int number => number,
            long number => checked((int)number),
            byte[] bytes when int.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var parsed) => parsed,
            _ => 0
        };
    }
}
