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
    RabbitMqMessageFailureAction FailureAction = RabbitMqMessageFailureAction.DeadLetter,
    RabbitMqFailure? FailureDetails = null)
{
    public static RabbitMqMessageProcessingResult Success() => new(true, null, null);
    public static RabbitMqMessageProcessingResult Success(byte[] outputBody) => new(true, outputBody, null);
    public static RabbitMqMessageProcessingResult Success(IReadOnlyList<RabbitMqMessageEnvelope> outputMessages) =>
        new(true, null, null, outputMessages);

    public static RabbitMqMessageProcessingResult Failure(string error) =>
        NonRetryableFailure(new RabbitMqFailure(RabbitMqFailureKind.Unknown, "unclassified", error));
    public static RabbitMqMessageProcessingResult NonRetryableFailure(string error) => Failure(error);
    public static RabbitMqMessageProcessingResult RetryableFailure(string error) =>
        RetryableFailure(new RabbitMqFailure(RabbitMqFailureKind.Unknown, "unclassified", error));
    public static RabbitMqMessageProcessingResult RetryableFailure(RabbitMqFailure failure) =>
        new(false, null, failure.Message, null, RabbitMqMessageFailureAction.Retry, failure);
    public static RabbitMqMessageProcessingResult NonRetryableFailure(RabbitMqFailure failure) =>
        new(false, null, failure.Message, null, RabbitMqMessageFailureAction.DeadLetter, failure);
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

