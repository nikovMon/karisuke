using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Observability;

[ProviderAlias("EcsHttp")]
internal sealed class EcsHttpLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly ConcurrentDictionary<string, EcsHttpLogger> _loggers = new(StringComparer.Ordinal);
    private readonly EcsLogBuffer _buffer;
    private readonly LogstashHttpOptions _options;
    private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
    private volatile bool _disposed;

    public EcsHttpLoggerProvider(EcsLogBuffer buffer, LogstashHttpOptions options)
    {
        _buffer = buffer;
        _options = options;
    }

    public ILogger CreateLogger(string categoryName)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _loggers.GetOrAdd(categoryName, category => new EcsHttpLogger(this, category));
    }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) =>
        _scopeProvider = scopeProvider ?? throw new ArgumentNullException(nameof(scopeProvider));

    public void Dispose()
    {
        _disposed = true;
        _loggers.Clear();
    }

    private void Enqueue<TState>(
        string category,
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (_disposed)
        {
            return;
        }

        string message;
        try
        {
            message = formatter(state, exception);
        }
        catch (Exception)
        {
            ObservabilityInternalTelemetry.RecordDroppedLogs("formatting");
            EcsEmergencyLog.Write($"A {logLevel} log record was dropped because its formatter failed.");
            return;
        }

        message = EcsLogValueNormalizer.TruncateText(message, _options.MaxStringLength);
        var attributes = new LogAttributeCollector(_options);
        try
        {
            _scopeProvider.ForEachScope(
                static (scope, collector) => collector.Add(scope),
                attributes);
            attributes.Add(state);
        }
        catch (Exception)
        {
            EcsEmergencyLog.Write("A structured log attribute could not be captured; the log record was preserved without that attribute.");
        }

        var activity = Activity.Current;
        var error = CaptureException(exception);
        var logEvent = new EcsLogEvent(
            DateTimeOffset.UtcNow,
            logLevel,
            category,
            eventId,
            message,
            error.Type,
            error.Message,
            error.StackTrace,
            activity is null ? null : activity.TraceId.ToHexString(),
            activity is null ? null : activity.SpanId.ToHexString(),
            Environment.CurrentManagedThreadId,
            attributes.Values);
        _buffer.TryWrite(logEvent);
    }

    private (string? Type, string? Message, string? StackTrace) CaptureException(Exception? exception)
    {
        if (exception is null)
        {
            return (null, null, null);
        }

        var type = exception.GetType().FullName ?? exception.GetType().Name;
        string message;
        try
        {
            message = exception.Message;
        }
        catch (Exception)
        {
            message = "Exception message was unavailable.";
        }

        string stackTrace;
        try
        {
            stackTrace = exception.ToString();
        }
        catch (Exception)
        {
            stackTrace = type;
        }

        return (
            EcsLogValueNormalizer.TruncateText(type, _options.MaxStringLength),
            EcsLogValueNormalizer.TruncateText(message, _options.MaxStringLength),
            EcsLogValueNormalizer.TruncateText(stackTrace, _options.MaxStringLength));
    }

    private sealed class EcsHttpLogger(EcsHttpLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            provider._scopeProvider.Push(state);

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            ArgumentNullException.ThrowIfNull(formatter);
            if (IsEnabled(logLevel))
            {
                provider.Enqueue(category, logLevel, eventId, state, exception, formatter);
            }
        }
    }

    private sealed class LogAttributeCollector
    {
        private readonly int _maxAttributeCount;
        private readonly int _maxCollectionCount;
        private readonly int _maxStringLength;
        private readonly Dictionary<string, object?> _values = new(StringComparer.Ordinal);

        public LogAttributeCollector(LogstashHttpOptions options)
        {
            _maxAttributeCount = options.MaxAttributeCount;
            _maxCollectionCount = options.MaxCollectionCount;
            _maxStringLength = options.MaxStringLength;
        }

        public IReadOnlyDictionary<string, object?> Values => _values;

        public void Add<TState>(TState state)
        {
            if (state is not IEnumerable<KeyValuePair<string, object?>> pairs)
            {
                return;
            }

            foreach (var pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair.Key)
                    || pair.Key.Equals("{OriginalFormat}", StringComparison.Ordinal)
                    || pair.Key.Equals("OriginalFormat", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!_values.ContainsKey(pair.Key) && _values.Count >= _maxAttributeCount)
                {
                    continue;
                }

                object? normalizedValue;
                try
                {
                    normalizedValue = EcsLogValueNormalizer.Normalize(
                        pair.Key,
                        pair.Value,
                        _maxCollectionCount,
                        _maxStringLength);
                }
                catch (Exception)
                {
                    normalizedValue = $"<unavailable:{pair.Value?.GetType().Name ?? "value"}>";
                }

                _values[pair.Key] = normalizedValue;
            }
        }
    }
}

internal sealed class EcsLogBuffer
{
    private readonly Channel<EcsLogEvent> _priority;
    private readonly Channel<EcsLogEvent> _warnings;
    private readonly Channel<EcsLogEvent> _normal;
    private readonly SemaphoreSlim _available = new(0, int.MaxValue);
    private int _completed;

    public EcsLogBuffer(LogstashHttpOptions options)
    {
        var normalCapacity = options.QueueCapacity
            - options.PriorityQueueCapacity
            - options.WarningQueueCapacity;
        _priority = CreateChannel(options.PriorityQueueCapacity);
        _warnings = CreateChannel(options.WarningQueueCapacity);
        _normal = CreateChannel(normalCapacity);
    }

    public bool IsCompleted => Volatile.Read(ref _completed) != 0;

    public bool IsDrained =>
        IsCompleted
        && !_priority.Reader.TryPeek(out _)
        && !_warnings.Reader.TryPeek(out _)
        && !_normal.Reader.TryPeek(out _);

    public bool TryWrite(EcsLogEvent logEvent)
    {
        if (IsCompleted)
        {
            ObservabilityInternalTelemetry.RecordDroppedLogs("shutdown");
            return false;
        }

        var written = logEvent.Level switch
        {
            LogLevel.Error or LogLevel.Critical =>
                _priority.Writer.TryWrite(logEvent)
                || _warnings.Writer.TryWrite(logEvent)
                || _normal.Writer.TryWrite(logEvent),
            LogLevel.Warning =>
                _warnings.Writer.TryWrite(logEvent)
                || _normal.Writer.TryWrite(logEvent),
            _ => _normal.Writer.TryWrite(logEvent)
        };
        if (!written)
        {
            var reason = logEvent.Level switch
            {
                LogLevel.Error or LogLevel.Critical => "priority_queues_full",
                LogLevel.Warning => "warning_queues_full",
                _ => "queue_full"
            };
            ObservabilityInternalTelemetry.RecordDroppedLogs(reason);
            if (logEvent.Level >= LogLevel.Warning)
            {
                EcsEmergencyLog.Write(
                    $"A {logEvent.Level} log record was dropped because its bounded Logstash queues were full.");
            }

            return false;
        }

        ObservabilityInternalTelemetry.AddQueuedLogs(1);
        _available.Release();
        return true;
    }

    public bool TryRead(out EcsLogEvent? logEvent)
    {
        logEvent = null;
        if (!_available.Wait(0))
        {
            return false;
        }

        if (TryTake(out logEvent))
        {
            return true;
        }

        return false;
    }

    public async ValueTask<EcsLogEvent?> ReadAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (!await _available.WaitAsync(timeout, cancellationToken))
        {
            return null;
        }

        return TryTake(out var logEvent) ? logEvent : null;
    }

    public void Complete()
    {
        if (Interlocked.Exchange(ref _completed, 1) != 0)
        {
            return;
        }

        _priority.Writer.TryComplete();
        _warnings.Writer.TryComplete();
        _normal.Writer.TryComplete();
        _available.Release();
    }

    public int DropRemaining()
    {
        var count = 0;
        while (TryRead(out _))
        {
            count++;
        }

        return count;
    }

    private bool TryTake(out EcsLogEvent? logEvent)
    {
        if (_priority.Reader.TryRead(out logEvent)
            || _warnings.Reader.TryRead(out logEvent)
            || _normal.Reader.TryRead(out logEvent))
        {
            ObservabilityInternalTelemetry.AddQueuedLogs(-1);
            return true;
        }

        logEvent = null;
        return false;
    }

    private static Channel<EcsLogEvent> CreateChannel(int capacity) =>
        Channel.CreateBounded<EcsLogEvent>(new BoundedChannelOptions(capacity)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
}
