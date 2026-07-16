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

public sealed record RabbitMqMessageProcessingResult(
    bool IsSuccess,
    byte[]? OutputBody,
    string? Error,
    IReadOnlyList<RabbitMqMessageEnvelope>? OutputMessages = null,
    RabbitMqMessageFailureAction FailureAction = RabbitMqMessageFailureAction.DeadLetter)
{
    public static RabbitMqMessageProcessingResult Success(byte[] outputBody) => new(true, outputBody, null);
    public static RabbitMqMessageProcessingResult Success(IReadOnlyList<RabbitMqMessageEnvelope> outputMessages) =>
        new(true, null, null, outputMessages);

    public static RabbitMqMessageProcessingResult Failure(string error) => new(false, null, error);
    public static RabbitMqMessageProcessingResult NonRetryableFailure(string error) => Failure(error);
    public static RabbitMqMessageProcessingResult RetryableFailure(string error) =>
        new(false, null, error, null, RabbitMqMessageFailureAction.Retry);
}

public enum RabbitMqMessageFailureAction
{
    DeadLetter,
    Retry
}

internal static class RabbitMqHeaders
{
    public static Dictionary<string, object?> Clone(IEnumerable<KeyValuePair<string, object?>>? headers) =>
        headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(headers, StringComparer.Ordinal);
}

