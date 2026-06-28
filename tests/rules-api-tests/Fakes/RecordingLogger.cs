using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Rules.Api.Tests.Fakes;

internal sealed class RecordingLogger<T> : ILogger<T>
{
    public List<LogEntry> Entries { get; } = [];
    public List<IReadOnlyDictionary<string, object?>> Scopes { get; } = [];

    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
    {
        if (state is IEnumerable<KeyValuePair<string, object?>> values)
        {
            Scopes.Add(values.ToDictionary(item => item.Key, item => item.Value, StringComparer.Ordinal));
        }

        return NoopDisposable.Instance;
    }

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

        Entries.Add(new LogEntry(logLevel, eventId, formatter(state, exception), exception, properties));
    }

    internal sealed record LogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
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
