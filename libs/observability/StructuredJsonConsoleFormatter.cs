using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Observability;

/// <summary>
/// A JSON console formatter that preserves arrays and AMQP string headers in
/// structured scopes. The framework JSON formatter otherwise writes arrays as CLR type names.
/// </summary>
internal sealed class StructuredJsonConsoleFormatter(IOptionsMonitor<JsonConsoleFormatterOptions> options)
    : ConsoleFormatter(FormatterName)
{
    public const string FormatterName = "structured-json";
    private const int MaxAttributes = 64;
    private const int MaxCollectionCount = 32;
    private const int MaxStringLength = 8_192;

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter(logEntry.State, logEntry.Exception);
        if (message is null && logEntry.Exception is null)
        {
            return;
        }

        var settings = options.CurrentValue;
        var document = new Dictionary<string, object?>
        {
            ["EventId"] = logEntry.EventId.Id,
            ["LogLevel"] = logEntry.LogLevel.ToString(),
            ["Category"] = logEntry.Category,
            ["Message"] = EcsLogValueNormalizer.TruncateText(message, MaxStringLength)
        };
        if (settings.TimestampFormat is { } timestampFormat)
        {
            var timestamp = settings.UseUtcTimestamp ? DateTimeOffset.UtcNow : DateTimeOffset.Now;
            document["Timestamp"] = timestamp.ToString(timestampFormat, CultureInfo.InvariantCulture);
        }

        if (logEntry.Exception is { } exception)
        {
            document["Exception"] = EcsLogValueNormalizer.TruncateText(exception.ToString(), MaxStringLength);
        }

        if (logEntry.State is not null)
        {
            document["State"] = NormalizeState(logEntry.State);
        }

        if (settings.IncludeScopes && scopeProvider is not null)
        {
            var scopes = new List<object?>();
            scopeProvider.ForEachScope(static (scope, target) => target.Add(NormalizeState(scope)), scopes);
            document["Scopes"] = scopes;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, settings.JsonWriterOptions))
        {
            JsonSerializer.Serialize(writer, document);
        }

        textWriter.WriteLine(Encoding.UTF8.GetString(buffer.WrittenSpan));
    }

    private static object? NormalizeState(object? state)
    {
        if (state is not IEnumerable<KeyValuePair<string, object?>> attributes)
        {
            return EcsLogValueNormalizer.Normalize("scope", state, MaxCollectionCount, MaxStringLength);
        }

        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var attribute in attributes.Take(MaxAttributes))
        {
            result[attribute.Key] = EcsLogValueNormalizer.Normalize(
                attribute.Key, attribute.Value, MaxCollectionCount, MaxStringLength);
        }

        return result;
    }
}
