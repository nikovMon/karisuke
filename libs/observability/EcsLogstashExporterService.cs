using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;

namespace ImagingPipeline.Observability;

internal sealed class EcsLogstashExporterService : IHostedService, IDisposable
{
    private readonly EcsLogBuffer _buffer;
    private readonly LogstashHttpOptions _options;
    private readonly ObservabilityResourceIdentity _resource;
    private readonly EcsLogDataStreamOptions _dataStream;
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _stop = new();
    private Task? _worker;
    private volatile bool _disposed;

    public EcsLogstashExporterService(
        EcsLogBuffer buffer,
        LogstashHttpOptions options,
        ObservabilityResourceIdentity resource,
        EcsLogDataStreamOptions dataStream)
        : this(
            buffer,
            options,
            resource,
            dataStream,
            new SocketsHttpHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
                MaxConnectionsPerServer = 2,
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            })
    {
    }

    internal EcsLogstashExporterService(
        EcsLogBuffer buffer,
        LogstashHttpOptions options,
        ObservabilityResourceIdentity resource,
        EcsLogDataStreamOptions dataStream,
        HttpMessageHandler handler)
    {
        _buffer = buffer;
        _options = options;
        _resource = resource;
        _dataStream = dataStream;
        _httpClient = new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _worker = Task.Run(() => RunAsync(_stop.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _buffer.Complete();
        if (_worker is null)
        {
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.ShutdownFlushTimeout);
        try
        {
            await _worker.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            _stop.Cancel();
            if (!cancellationToken.IsCancellationRequested)
            {
                EcsEmergencyLog.Write(
                    "Timed out while flushing the bounded Logstash log buffer during shutdown.");
            }

            ObserveWorker(_worker);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _buffer.Complete();
        _stop.Cancel();
        _stop.Dispose();
        _httpClient.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<EcsLogEvent>(_options.BatchSize);
        try
        {
            while (!cancellationToken.IsCancellationRequested && !_buffer.IsDrained)
            {
                try
                {
                    await RunCoreAsync(batch, cancellationToken);
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    RecordBatchDrop(batch, "shutdown");
                    break;
                }
                catch (Exception exception)
                {
                    RecordBatchDrop(batch, "exporter_failure");
                    EcsEmergencyLog.Write(
                        $"The Logstash exporter failed unexpectedly and will restart: {exception.GetType().Name}.");
                    try
                    {
                        await Task.Delay(_options.RetryBaseDelay, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            RecordBatchDrop(
                batch,
                cancellationToken.IsCancellationRequested ? "shutdown" : "exporter_failure");
            var dropped = _buffer.DropRemaining();
            if (dropped > 0)
            {
                var reason = cancellationToken.IsCancellationRequested || _buffer.IsCompleted
                    ? "shutdown"
                    : "exporter_failure";
                ObservabilityInternalTelemetry.RecordDroppedLogs(reason, dropped);
                EcsEmergencyLog.Write(
                    $"The Logstash exporter discarded {dropped} buffered log records during {reason}.");
            }
        }
    }

    private async Task RunCoreAsync(List<EcsLogEvent> batch, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            batch.Clear();
            if (_buffer.IsDrained)
            {
                break;
            }

            var first = await _buffer.ReadAsync(Timeout.InfiniteTimeSpan, cancellationToken);
            if (first is null)
            {
                if (_buffer.IsDrained)
                {
                    break;
                }

                continue;
            }

            batch.Add(first);
            var deadline = Stopwatch.GetTimestamp() + (long)(
                _options.FlushInterval.TotalSeconds * Stopwatch.Frequency);

            while (batch.Count < _options.BatchSize)
            {
                while (batch.Count < _options.BatchSize && _buffer.TryRead(out var next))
                {
                    batch.Add(next!);
                }

                if (batch.Count >= _options.BatchSize || _buffer.IsDrained)
                {
                    break;
                }

                var remainingTicks = deadline - Stopwatch.GetTimestamp();
                if (remainingTicks <= 0)
                {
                    break;
                }

                var remaining = TimeSpan.FromSeconds((double)remainingTicks / Stopwatch.Frequency);
                var nextEvent = await _buffer.ReadAsync(remaining, cancellationToken);
                if (nextEvent is null)
                {
                    break;
                }

                batch.Add(nextEvent);
            }

            await ExportAsync(batch, cancellationToken);
            batch.Clear();
        }

        while (_buffer.TryRead(out var remaining))
        {
            batch.Clear();
            batch.Add(remaining!);
            while (batch.Count < _options.BatchSize && _buffer.TryRead(out remaining))
            {
                batch.Add(remaining!);
            }

            await ExportAsync(batch, cancellationToken);
            batch.Clear();
        }
    }

    private async Task ExportAsync(
        IReadOnlyList<EcsLogEvent> batch,
        CancellationToken cancellationToken)
    {
        byte[] payload;
        try
        {
            payload = EcsLogDocumentSerializer.SerializeBatch(batch, _resource, _dataStream);
        }
        catch (Exception exception)
        {
            ObservabilityInternalTelemetry.RecordDroppedLogs("serialization", batch.Count);
            EcsEmergencyLog.Write(
                $"Failed to serialize {batch.Count} log records for Logstash: {exception.GetType().Name}.");
            return;
        }

        for (var attempt = 0; attempt <= _options.MaxRetryAttempts; attempt++)
        {
            var started = TelemetryTiming.StartTimestamp();
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                request.Content = new ByteArrayContent(payload);
                request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
                {
                    CharSet = "utf-8"
                };

                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(_options.RequestTimeout);
                using var suppressionScope = SuppressInstrumentationScope.Begin();
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    timeout.Token);
                var statusCodeClass = $"{(int)response.StatusCode / 100}xx";
                if (response.IsSuccessStatusCode)
                {
                    ObservabilityInternalTelemetry.RecordExport(
                        TelemetryTiming.ElapsedSeconds(started),
                        "success",
                        statusCodeClass);
                    return;
                }

                var retryable = response.StatusCode is HttpStatusCode.RequestTimeout
                    or HttpStatusCode.TooManyRequests
                    || (int)response.StatusCode >= 500;
                ObservabilityInternalTelemetry.RecordExport(
                    TelemetryTiming.ElapsedSeconds(started),
                    retryable ? "retry" : "failure",
                    statusCodeClass);
                if (!retryable)
                {
                    break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ObservabilityInternalTelemetry.RecordDroppedLogs("shutdown", batch.Count);
                return;
            }
            catch (OperationCanceledException)
            {
                ObservabilityInternalTelemetry.RecordExport(
                    TelemetryTiming.ElapsedSeconds(started),
                    "retry");
            }
            catch (HttpRequestException)
            {
                ObservabilityInternalTelemetry.RecordExport(
                    TelemetryTiming.ElapsedSeconds(started),
                    "retry");
            }

            if (attempt < _options.MaxRetryAttempts)
            {
                var exponent = Math.Min(attempt, 10);
                var baseDelayMilliseconds = _options.RetryBaseDelay.TotalMilliseconds * (1 << exponent);
                var jitterMilliseconds = Random.Shared.NextDouble() * _options.RetryBaseDelay.TotalMilliseconds;
                await Task.Delay(
                    TimeSpan.FromMilliseconds(baseDelayMilliseconds + jitterMilliseconds),
                    cancellationToken);
            }
        }

        ObservabilityInternalTelemetry.RecordDroppedLogs("transport", batch.Count);
        EcsEmergencyLog.Write(
            $"Logstash did not accept a batch of {batch.Count} log records after {_options.MaxRetryAttempts + 1} attempts.");
    }

    private static void RecordBatchDrop(List<EcsLogEvent> batch, string reason)
    {
        if (batch.Count == 0)
        {
            return;
        }

        ObservabilityInternalTelemetry.RecordDroppedLogs(reason, batch.Count);
        batch.Clear();
    }
    private static void ObserveWorker(Task worker)
    {
        _ = worker.ContinueWith(
            static completed => _ = completed.Exception,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }
}
