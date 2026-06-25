namespace ImagingPipeline.RabbitMqClient;

public sealed record RabbitMqMessageEnvelope(
    string MessageId,
    byte[] Body,
    string ContentType = "application/json",
    IReadOnlyDictionary<string, object?>? Headers = null,
    string? CorrelationId = null)
{
    public static RabbitMqMessageEnvelope FromUtf8(string body, string? messageId = null) =>
        new(messageId ?? Guid.NewGuid().ToString("N"), System.Text.Encoding.UTF8.GetBytes(body));

    public string BodyAsUtf8() => System.Text.Encoding.UTF8.GetString(Body);
}

public sealed record RabbitMqMessageProcessingResult(bool IsSuccess, byte[]? OutputBody, string? Error)
{
    public static RabbitMqMessageProcessingResult Success(byte[] outputBody) => new(true, outputBody, null);
    public static RabbitMqMessageProcessingResult Failure(string error) => new(false, null, error);
}

