using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Observability;

internal sealed record ObservabilityResourceIdentity(
    string ServiceName,
    string ServiceNamespace,
    string ServiceVersion,
    string DeploymentEnvironment,
    string ServiceInstanceId,
    string? PodName,
    string? PodUid,
    string? PodNamespace,
    string? DeploymentName,
    string? NodeName,
    string? ContainerName)
{
    public string? ClusterName { get; init; } =
        ReadOptionalEnvironmentVariable("CLUSTER_NAME");

    public string HostName { get; } = Environment.MachineName;
    public int ProcessId { get; } = Environment.ProcessId;
    public string RuntimeName { get; } = ".NET";
    public string RuntimeVersion { get; } = Environment.Version.ToString();
    public string RuntimeDescription { get; } = RuntimeInformation.FrameworkDescription;

    private static string? ReadOptionalEnvironmentVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}

internal sealed record EcsLogDataStreamOptions(string Dataset, string Namespace);

internal sealed record LogstashHttpOptions(
    Uri Endpoint,
    int QueueCapacity,
    int PriorityQueueCapacity,
    int WarningQueueCapacity,
    int BatchSize,
    TimeSpan FlushInterval,
    TimeSpan RequestTimeout,
    int MaxRetryAttempts,
    TimeSpan RetryBaseDelay,
    TimeSpan ShutdownFlushTimeout,
    int MaxAttributeCount,
    int MaxCollectionCount,
    int MaxStringLength)
{
    private const string Prefix = "Observability:Logs:Logstash";
    private static readonly Regex DataStreamPartPattern = new(
        "^[a-z0-9_.]+$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static LogstashHttpOptions Read(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var endpointValue = configuration[$"{Prefix}:Endpoint"];
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            throw new InvalidOperationException(
                $"Configuration value '{Prefix}:Endpoint' is required when Logstash HTTP logging is enabled.");
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new InvalidOperationException(
                $"Configuration value '{Prefix}:Endpoint' must be an absolute HTTP or HTTPS URL.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException(
                $"Configuration value '{Prefix}:Endpoint' must not contain credentials.");
        }

        var queueCapacity = ReadInt(configuration, $"{Prefix}:QueueCapacity", 10_000, 100, 1_000_000);
        var priorityQueueCapacity = ReadInt(
            configuration,
            $"{Prefix}:PriorityQueueCapacity",
            Math.Min(1_000, queueCapacity / 2),
            1,
            queueCapacity - 2);
        var warningQueueCapacity = ReadInt(
            configuration,
            $"{Prefix}:WarningQueueCapacity",
            Math.Min(1_000, (queueCapacity - priorityQueueCapacity) / 2),
            1,
            queueCapacity - priorityQueueCapacity - 1);
        var batchSize = ReadInt(
            configuration,
            $"{Prefix}:BatchSize",
            100,
            1,
            Math.Min(queueCapacity, 5_000));

        return new LogstashHttpOptions(
            endpoint,
            queueCapacity,
            priorityQueueCapacity,
            warningQueueCapacity,
            batchSize,
            TimeSpan.FromMilliseconds(
                ReadInt(configuration, $"{Prefix}:FlushIntervalMilliseconds", 1_000, 50, 60_000)),
            TimeSpan.FromSeconds(
                ReadInt(configuration, $"{Prefix}:RequestTimeoutSeconds", 5, 1, 120)),
            ReadInt(configuration, $"{Prefix}:MaxRetryAttempts", 3, 0, 10),
            TimeSpan.FromMilliseconds(
                ReadInt(configuration, $"{Prefix}:RetryBaseDelayMilliseconds", 200, 10, 30_000)),
            TimeSpan.FromSeconds(
                ReadInt(configuration, $"{Prefix}:ShutdownFlushTimeoutSeconds", 5, 1, 120)),
            ReadInt(configuration, $"{Prefix}:MaxAttributeCount", 64, 1, 256),
            ReadInt(configuration, $"{Prefix}:MaxCollectionCount", 32, 1, 256),
            ReadInt(configuration, $"{Prefix}:MaxStringLength", 8_192, 256, 65_536));
    }

    public static EcsLogDataStreamOptions ReadDataStream(
        IConfiguration configuration,
        string deploymentEnvironment)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var dataset = configuration["Observability:Logs:DataStream:Dataset"]?.Trim()
            ?? "findair";
        var dataStreamNamespace = configuration["Observability:Logs:DataStream:Namespace"]?.Trim()
            ?? deploymentEnvironment.Trim().ToLowerInvariant();

        ValidateDataStreamPart(
            dataset,
            "Observability:Logs:DataStream:Dataset");
        ValidateDataStreamPart(
            dataStreamNamespace,
            "Observability:Logs:DataStream:Namespace");
        return new EcsLogDataStreamOptions(dataset, dataStreamNamespace);
    }

    private static void ValidateDataStreamPart(string value, string configurationKey)
    {
        if (value.Length is < 1 or > 100 || !DataStreamPartPattern.IsMatch(value))
        {
            throw new InvalidOperationException(
                $"Configuration value '{configurationKey}' must contain only lowercase letters, digits, underscores, or dots; must not contain a hyphen; and must be at most 100 characters.");
        }
    }

    private static int ReadInt(
        IConfiguration configuration,
        string key,
        int defaultValue,
        int minimum,
        int maximum)
    {
        var raw = configuration[key];
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            && value >= minimum
            && value <= maximum)
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Configuration value '{key}' must be an integer from {minimum} through {maximum}.");
    }
}

internal sealed record EcsLogEvent(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    EventId EventId,
    string Message,
    string? ErrorType,
    string? ErrorMessage,
    string? ErrorStackTrace,
    string? TraceId,
    string? SpanId,
    int ThreadId,
    IReadOnlyDictionary<string, object?> Attributes);

internal static class EcsLogValueNormalizer
{
    public static object? Normalize(
        string key,
        object? value,
        int maxCollectionCount,
        int maxStringLength,
        int depth = 0)
    {
        if (value is null)
        {
            return null;
        }

        if (depth >= 3)
        {
            return Truncate(Convert.ToString(value, CultureInfo.InvariantCulture), maxStringLength);
        }

        return value switch
        {
            string text => NormalizeString(key, text, maxStringLength),
            char character => character.ToString(),
            bool or byte or sbyte or short or ushort or int or uint or long or ulong
                or decimal => value,
            float number => float.IsFinite(number) ? number : null,
            double number => double.IsFinite(number) ? number : null,
            Enum enumeration => enumeration.ToString(),
            Guid guid => guid.ToString("D"),
            DateTime dateTime => dateTime.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            DateTimeOffset dateTimeOffset => dateTimeOffset.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
            TimeSpan timeSpan => timeSpan.TotalMilliseconds,
            Uri uri => SafeUri(uri),
            byte[] bytes => NormalizeBytes(bytes, maxStringLength),
            Memory<byte> memory => NormalizeBytes(memory.Span, maxStringLength),
            ReadOnlyMemory<byte> memory => NormalizeBytes(memory.Span, maxStringLength),
            IEnumerable<KeyValuePair<string, object?>> pairs => NormalizePairs(
                pairs,
                maxCollectionCount,
                maxStringLength,
                depth + 1),
            System.Collections.IEnumerable values => NormalizeValues(
                values,
                maxCollectionCount,
                maxStringLength,
                depth + 1),
            _ => NormalizeUnknown(value, maxStringLength)
        };
    }

    public static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var builder = new StringBuilder(value.Length + 8);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (char.IsUpper(character))
            {
                if (index > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }

                builder.Append(char.ToLowerInvariant(character));
            }
            else if (character is '-' or ' ' or '.')
            {
                if (builder.Length > 0 && builder[^1] != '_')
                {
                    builder.Append('_');
                }
            }
            else if (char.IsLetterOrDigit(character) || character == '_')
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.Length == 0 ? "value" : builder.ToString();
    }

    public static string TruncateText(string? value, int maxLength) =>
        Truncate(value, maxLength) ?? string.Empty;

    private static object NormalizeUnknown(object value, int maxStringLength)
    {
        try
        {
            return Truncate(Convert.ToString(value, CultureInfo.InvariantCulture), maxStringLength)
                ?? value.GetType().Name;
        }
        catch (Exception)
        {
            return value.GetType().Name;
        }
    }

    private static string NormalizeString(string key, string value, int maxStringLength)
    {
        if ((key.Contains("uri", StringComparison.OrdinalIgnoreCase)
                || key.Contains("url", StringComparison.OrdinalIgnoreCase))
            && Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            return SafeUri(uri);
        }

        return Truncate(value, maxStringLength) ?? string.Empty;
    }

    private static string SafeUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
        {
            return uri.ToString();
        }

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            builder.UserName = "REDACTED";
            builder.Password = "REDACTED";
        }

        return builder.Uri.AbsoluteUri;
    }

    private static string NormalizeBytes(ReadOnlySpan<byte> bytes, int maxStringLength)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            if (text.All(static character => !char.IsControl(character) || char.IsWhiteSpace(character)))
            {
                return Truncate(text, maxStringLength) ?? string.Empty;
            }
        }
        catch (DecoderFallbackException)
        {
            // Binary values are represented explicitly instead of as System.Byte[].
        }

        var availableCharacters = Math.Max(0, maxStringLength - "base64:".Length);
        var maxBytes = Math.Min(bytes.Length, availableCharacters / 4 * 3);
        if (maxBytes == 0)
        {
            return "base64:"[..Math.Min("base64:".Length, maxStringLength)];
        }

        return $"base64:{Convert.ToBase64String(bytes[..maxBytes])}";
    }

    private static IReadOnlyDictionary<string, object?> NormalizePairs(
        IEnumerable<KeyValuePair<string, object?>> pairs,
        int maxCollectionCount,
        int maxStringLength,
        int depth)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var pair in pairs.Take(maxCollectionCount))
        {
            result[pair.Key] = Normalize(
                pair.Key,
                pair.Value,
                maxCollectionCount,
                maxStringLength,
                depth);
        }

        return result;
    }

    private static IReadOnlyList<object?> NormalizeValues(
        System.Collections.IEnumerable values,
        int maxCollectionCount,
        int maxStringLength,
        int depth)
    {
        var result = new List<object?>();
        foreach (var value in values)
        {
            if (result.Count >= maxCollectionCount)
            {
                break;
            }

            result.Add(Normalize(
                "value",
                value,
                maxCollectionCount,
                maxStringLength,
                depth));
        }

        return result;
    }

    private static string? Truncate(string? value, int maxLength) =>
        value is { Length: > 0 } && value.Length > maxLength
            ? value[..maxLength]
            : value;
}
