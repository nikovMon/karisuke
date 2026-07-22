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

        var headers = RabbitMqHeaders.Clone(message.Headers);

        if (!TryReadRetryCount(headers, retryCountHeader, out var currentRetryCount))
        {
            return Invalid(
                $"Retry count header '{retryCountHeader}' must contain one unambiguous non-negative integer.");
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
        foreach (var key in headers.Keys
                     .Where(key => string.Equals(key, retryCountHeader, StringComparison.OrdinalIgnoreCase))
                     .ToArray())
        {
            headers.Remove(key);
        }
        headers[retryCountHeader] = nextRetryCount;
        return new RabbitMqRetryBuildResult(
            RabbitMqRetryBuildStatus.Retry,
            message with { Headers = headers },
            currentRetryCount,
            nextRetryCount,
            null);
    }

    internal static bool TryReadRetryCount(object? value, out int retryCount)
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
            case ReadOnlyMemory<byte> bytes:
                return TryReadRetryCountText(Encoding.UTF8.GetString(bytes.Span), out retryCount);
            case Memory<byte> bytes:
                return TryReadRetryCountText(Encoding.UTF8.GetString(bytes.Span), out retryCount);
            default:
                return false;
        }
    }

    internal static bool TryReadRetryCount(
        IReadOnlyDictionary<string, object?>? headers,
        string retryCountHeader,
        out int retryCount)
    {
        retryCount = 0;
        if (headers is null)
        {
            return true;
        }

        var found = false;
        foreach (var pair in headers)
        {
            if (!string.Equals(pair.Key, retryCountHeader, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!TryReadRetryCount(pair.Value, out var candidate)
                || found && candidate != retryCount)
            {
                retryCount = 0;
                return false;
            }

            retryCount = candidate;
            found = true;
        }

        return true;
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
