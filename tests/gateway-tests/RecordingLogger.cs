using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Gateway.Tests;

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull => NoopDisposable.Instance;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        var properties = state is IEnumerable<KeyValuePair<string, object?>> values
            ? values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal)
            : new Dictionary<string, object?>(StringComparer.Ordinal);

        Entries.Add(new LogEntry(logLevel, eventId, exception, properties));
    }

    internal sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        Exception? Exception,
        IReadOnlyDictionary<string, object?> Properties);

    private sealed class NoopDisposable : IDisposable
    {
        public static readonly NoopDisposable Instance = new();

        public void Dispose()
        {
        }
    }
}
