using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class RecordingLoggerProvider : ILoggerProvider
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    private readonly LoggerExternalScopeProvider _scopes = new();

    public ILogger CreateLogger(string categoryName) => new RecordingLogger(categoryName, Entries, _scopes);

    public void Dispose()
    {
    }

    internal sealed record LogEntry(
        string Category,
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    /// <summary>Records scope fields as entry properties, the way the ECS serializer flattens them.</summary>
    private sealed class RecordingLogger(
        string category,
        ConcurrentQueue<LogEntry> entries,
        LoggerExternalScopeProvider scopes) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => scopes.Push(state);

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal);
            scopes.ForEachScope(static (scope, fields) =>
            {
                if (scope is IEnumerable<KeyValuePair<string, object?>> values)
                {
                    foreach (var item in values)
                    {
                        fields[item.Key] = item.Value;
                    }
                }
            }, properties);

            if (state is IEnumerable<KeyValuePair<string, object?>> values)
            {
                foreach (var item in values)
                {
                    properties[item.Key] = item.Value;
                }
            }

            entries.Enqueue(new LogEntry(
                category,
                logLevel,
                eventId,
                formatter(state, exception),
                exception,
                properties));
        }
    }
}
