using System.Globalization;
using System.Text;

namespace ImagingPipeline.RabbitMqClient;

internal enum RabbitMqRetryBuildStatus
{
    Retry,
    AttemptsExhausted,
    InvalidMessage
}

internal sealed record RabbitMqRetryBuildResult(
    RabbitMqRetryBuildStatus Status,
    RabbitMqMessageEnvelope? Message,
    int CurrentRetryCount,
    int NextRetryCount,
    string? Error);

internal static class RabbitMqRetryMessageBuilder
{
    public static RabbitMqRetryBuildResult Build(
        RabbitMqMessageEnvelope message,
        string retryCountHeader,
        int maxRetryAttempts)
    {
        if (string.IsNullOrWhiteSpace(retryCountHeader))
        {
            return Invalid("Retry count header must not be empty.");
        }

        var headers = message.Headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(message.Headers, StringComparer.Ordinal);

        var currentRetryCount = 0;
        if (headers.TryGetValue(retryCountHeader, out var retryCountHeaderValue) &&
            !TryReadRetryCount(retryCountHeaderValue, out currentRetryCount))
        {
            return Invalid($"Retry count header '{retryCountHeader}' must contain a non-negative integer.");
        }

        if (currentRetryCount >= maxRetryAttempts)
        {
            return new RabbitMqRetryBuildResult(
                RabbitMqRetryBuildStatus.AttemptsExhausted,
                null,
                currentRetryCount,
                currentRetryCount,
                null);
        }

        var nextRetryCount = currentRetryCount + 1;
        headers[retryCountHeader] = nextRetryCount;
        return new RabbitMqRetryBuildResult(
            RabbitMqRetryBuildStatus.Retry,
            message with { Headers = headers },
            currentRetryCount,
            nextRetryCount,
            null);
    }

    private static bool TryReadRetryCount(object? value, out int retryCount)
    {
        retryCount = 0;

        switch (value)
        {
            case null:
                return true;
            case byte number:
                retryCount = number;
                return true;
            case sbyte number when number >= 0:
                retryCount = number;
                return true;
            case short number when number >= 0:
                retryCount = number;
                return true;
            case ushort number:
                retryCount = number;
                return true;
            case int number when number >= 0:
                retryCount = number;
                return true;
            case uint number when number <= int.MaxValue:
                retryCount = (int)number;
                return true;
            case long number when number is >= 0 and <= int.MaxValue:
                retryCount = (int)number;
                return true;
            case ulong number when number <= int.MaxValue:
                retryCount = (int)number;
                return true;
            case string text:
                return TryReadRetryCountText(text, out retryCount);
            case byte[] bytes:
                return TryReadRetryCountText(Encoding.UTF8.GetString(bytes), out retryCount);
            default:
                return false;
        }
    }

    private static bool TryReadRetryCountText(string text, out int retryCount) =>
        int.TryParse(
            text,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out retryCount) &&
        retryCount >= 0;

    private static RabbitMqRetryBuildResult Invalid(string error) =>
        new(
            RabbitMqRetryBuildStatus.InvalidMessage,
            null,
            0,
            0,
            error);
}
