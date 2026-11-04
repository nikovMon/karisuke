using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace ImagingPipeline.RabbitMqClient;

public enum RabbitMqFailureKind
{
    Transient,
    InvalidData,
    DependencyResponse,
    Configuration,
    Unknown
}

public sealed record RabbitMqFailure(
    RabbitMqFailureKind Kind,
    string Code,
    string Message,
    string? Dependency = null,
    string? Operation = null,
    int? HttpStatus = null);

/// <summary>Failure metadata belongs to one service's retained input, not its successful output.</summary>
public static class RabbitMqFailureHeaders
{
    public const string FailureClass = "failure-class";
    public const string FailureCode = "failure-code";
    public const string FailureMessage = "failure-message";
    public const string FailureService = "failure-service";
    public const string Dependency = "dependency";
    public const string Operation = "operation";
    public const string HttpStatus = "http-status";
    public const string FirstFailureAt = "first-failure-at";
    public const string LastFailureAt = "last-failure-at";
    public const string RedriveCount = "redrive-count";
    public const string LastRedriveAt = "last-redrive-at";
    public const string RedriveRunId = "redrive-run-id";
    public const string FailureMessageTruncated = "failure-message-truncated";

    public static string ToToken(RabbitMqFailureKind kind) => kind switch
    {
        RabbitMqFailureKind.Transient => "transient",
        RabbitMqFailureKind.InvalidData => "invalid-data",
        RabbitMqFailureKind.DependencyResponse => "dependency-response",
        RabbitMqFailureKind.Configuration => "configuration",
        _ => "unknown"
    };

    public static RabbitMqMessageEnvelope Enrich(
        RabbitMqMessageEnvelope message,
        RabbitMqFailure failure,
        string service,
        DateTimeOffset? now = null)
    {
        var headers = RabbitMqHeaders.Clone(message.Headers);
        var timestamp = (now ?? DateTimeOffset.UtcNow).ToString("O", CultureInfo.InvariantCulture);
        Set(headers, FailureClass, ToToken(failure.Kind));
        Set(headers, FailureCode, Bound(failure.Code, 256));
        Set(headers, FailureMessage, Bound(failure.Message, 4096));
        Set(headers, FailureService, Bound(service, 256));
        Set(headers, Dependency, failure.Dependency is null ? null : Bound(failure.Dependency, 256));
        Set(headers, Operation, failure.Operation is null ? null : Bound(failure.Operation, 256));
        Set(headers, HttpStatus, failure.HttpStatus);
        Set(headers, FailureMessageTruncated, failure.Message.Length > 4096 ? true : null);
        if (!TryReadText(headers, FirstFailureAt, out var first) ||
            !DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            Set(headers, FirstFailureAt, timestamp);
        }
        Set(headers, LastFailureAt, timestamp);
        return message with { Headers = headers };
    }

    public static bool TryReadFailure(IReadOnlyDictionary<string, object?>? headers, out RabbitMqFailure failure)
    {
        failure = new(RabbitMqFailureKind.Unknown, "unclassified", "Failure metadata is missing or invalid.");
        if (!TryReadText(headers, FailureClass, out var token) ||
            !TryReadText(headers, FailureCode, out var code) || string.IsNullOrWhiteSpace(code) ||
            !TryReadText(headers, FailureMessage, out var message))
        {
            return false;
        }

        var kind = token switch
        {
            "transient" => RabbitMqFailureKind.Transient,
            "invalid-data" => RabbitMqFailureKind.InvalidData,
            "dependency-response" => RabbitMqFailureKind.DependencyResponse,
            "configuration" => RabbitMqFailureKind.Configuration,
            "unknown" => RabbitMqFailureKind.Unknown,
            _ => (RabbitMqFailureKind?)null
        };
        if (kind is null)
        {
            return false;
        }

        TryReadText(headers, Dependency, out var dependency);
        TryReadText(headers, Operation, out var operation);
        int? status = TryReadNonNegativeInt(headers, HttpStatus, out var number) && number is >= 100 and <= 599
            ? number : null;
        failure = new(kind.Value, code, message, dependency, operation, status);
        return true;
    }

    /// <summary>Missing count means zero. Conflicting case variants and malformed values are rejected.</summary>
    public static bool TryReadNonNegativeInt(IReadOnlyDictionary<string, object?>? headers, string name, out int value)
    {
        value = 0;
        return headers?.Any(pair => string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase) && pair.Value is null) != true &&
               RabbitMqRetryMessageBuilder.TryReadRetryCount(headers, name, out value);
    }

    public static bool TryReadText(IReadOnlyDictionary<string, object?>? headers, string name, [NotNullWhen(true)] out string? value)
    {
        value = null;
        if (headers is null)
        {
            return false;
        }
        var found = false;
        foreach (var pair in headers)
        {
            if (!string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            var candidate = pair.Value switch
            {
                string text => text,
                byte[] bytes => Encoding.UTF8.GetString(bytes),
                ReadOnlyMemory<byte> bytes => Encoding.UTF8.GetString(bytes.Span),
                _ => null
            };
            if (candidate is null || found && !string.Equals(candidate, value, StringComparison.Ordinal))
            {
                value = null;
                return false;
            }
            found = true;
            value = candidate;
        }
        return found;
    }

    private static void Set(Dictionary<string, object?> headers, string name, object? value)
    {
        foreach (var key in headers.Keys.Where(key => string.Equals(key, name, StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            headers.Remove(key);
        }
        if (value is not null)
        {
            headers[name] = value;
        }
    }

    private static string Bound(string value, int length) => value.Length <= length ? value : value[..length];
}
