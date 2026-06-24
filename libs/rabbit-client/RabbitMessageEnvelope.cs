namespace ImagingPipeline.RabbitClient;

public sealed record RabbitMessageEnvelope(
    string MessageId,
    byte[] Body,
    string ContentType = "application/json",
    IReadOnlyDictionary<string, object?>? Headers = null,
    string? CorrelationId = null)
{
    public static RabbitMessageEnvelope FromUtf8(string body, string? messageId = null) =>
        new(messageId ?? Guid.NewGuid().ToString("N"), System.Text.Encoding.UTF8.GetBytes(body));

    public string BodyAsUtf8() => System.Text.Encoding.UTF8.GetString(Body);
}

public sealed record RabbitMessageProcessingResult(bool IsSuccess, byte[]? OutputBody, string? Error)
{
    public static RabbitMessageProcessingResult Success(byte[] outputBody) => new(true, outputBody, null);
    public static RabbitMessageProcessingResult Failure(string error) => new(false, null, error);
}

