using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net;
using System.Text.Json;
using ImagingPipeline.Observability;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public sealed partial class ElasticOverlayProvider
{
    private const int ErrorBodyLimit = 8192;
    private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(60);
    private static readonly Counter<long> RequestsMetric = TelemetryMeters.RulesApi.CreateCounter<long>("auto_run.provider.requests");
    private static readonly Counter<long> RetriesMetric = TelemetryMeters.RulesApi.CreateCounter<long>("auto_run.provider.retries");
    private static readonly Histogram<double> DurationMetric = TelemetryMeters.RulesApi.CreateHistogram<double>("auto_run.provider.request.duration", "ms");
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private long? _lastAttemptFinished;

    private async Task<JsonDocument?> SendAsync(HttpRequestMessage original, string operation,
        bool allowNotFound, CancellationToken cancellationToken, Action<ProviderProgress>? report)
    {
        ValidateTransportOptions();
        memory?.ThrowIfUnsafe();
        var started = _time.GetTimestamp();
        var body = original.Content is null ? null : await original.Content.ReadAsByteArrayAsync(cancellationToken);
        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            var retryDelay = TimeSpan.Zero;
            int? lastStatus = null;
            for (var attempt = 1; attempt <= _options.MaxRetries + 1; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pacing = _lastAttemptFinished is { } finished
                    ? TimeSpan.FromMilliseconds(_options.RequestDelayMs) - _time.GetElapsedTime(finished)
                    : TimeSpan.Zero;
                var delay = pacing > retryDelay ? pacing : retryDelay;
                if (delay > TimeSpan.Zero)
                {
                    report?.Invoke(new(attempt == 1 ? "requestDelay" : "retryWait", operation,
                        attempt, _options.MaxRetries + 1, lastStatus, _time.GetUtcNow() + delay));
                    await Task.Delay(delay, _time, cancellationToken);
                }
                cancellationToken.ThrowIfCancellationRequested();
                memory?.ThrowIfUnsafe();
                report?.Invoke(new("requesting", operation, attempt, _options.MaxRetries + 1, lastStatus));
                using var request = CloneRequest(original, body);
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds), _time);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
                var phase = "waiting for response headers";
                long bytesRead = 0;
                int? statusCode = null;
                var outcome = "failed";
                var attemptStarted = _time.GetTimestamp();
                try
                {
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    statusCode = (int)response.StatusCode;
                    phase = "reading the response body";
                    if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                    {
                        outcome = "notFound";
                        return null;
                    }
                    if (!response.IsSuccessStatusCode)
                    {
                        var errorBody = await ReadErrorBodyAsync(response, request.RequestUri!, timeout.Token, cancellationToken);
                        bytesRead = errorBody.BytesRead;
                        outcome = "httpError";
                        var status = (int)response.StatusCode;
                        var retryable = status is 429 or 503;
                        var serverDelay = RetryAfter(response);
                        var tooLong = retryable && serverDelay > MaximumRetryDelay;
                        var message = $"Elastic Provider returned HTTP {status} during {operation} after {attempt} attempt(s).";
                        if (tooLong) message += " Retry-After exceeds the maximum allowed 60-second wait.";
                        var diagnostic = Diagnostic(retryable ? "providerUnavailable" : "providerHttpError", message,
                            operation, request.Method.Method, status, attempt, started, errorBody, bytesRead: errorBody.BytesRead);
                        if (!retryable) throw new ElasticProviderException(message, diagnostic: diagnostic);
                        if (attempt > _options.MaxRetries || tooLong)
                            throw new ProviderUnavailableException(message, diagnostic);
                        retryDelay = TimeSpan.FromMilliseconds(Math.Min(60_000L,
                            (long)_options.RetryDelayMs * (1L << (attempt - 1))));
                        if (serverDelay > retryDelay) retryDelay = serverDelay;
                        lastStatus = status;
                        logger?.LogWarning("Elastic Provider returned HTTP {StatusCode} during {Operation}; retry {Attempt} of {MaxAttempts} follows after at least {DelayMs} ms.",
                            status, operation, attempt + 1, _options.MaxRetries + 1,
                            Math.Max(_options.RequestDelayMs, retryDelay.TotalMilliseconds));
                        continue;
                    }
                    if (response.RequestMessage?.RequestUri is { } effective && !SameOrigin(effective, TableUri()))
                        throw new ElasticProviderException("Provider response redirected outside the configured origin.");
                    if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                        throw new ProviderResponseLimitException(_options.MaxResponseBytes);
                    if (response.Content.Headers.ContentLength is > 0 and var length)
                        memory?.ThrowIfUnsafe(length * 3);
                    await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var buffer = new MemoryStream();
                    var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
                    try
                    {
                        int read;
                        while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token)) != 0)
                        {
                            bytesRead += read;
                            var required = buffer.Length + read;
                            if (required > _options.MaxResponseBytes)
                                throw new ProviderResponseLimitException(_options.MaxResponseBytes);
                            if (required > buffer.Capacity)
                            {
                                var capacity = Math.Min(_options.MaxResponseBytes,
                                    Math.Max(required, Math.Max(256L, 2L * buffer.Capacity)));
                                memory?.ThrowIfUnsafe(capacity + required * 2);
                                buffer.Capacity = checked((int)capacity);
                            }
                            buffer.Write(chunk, 0, read);
                        }
                        memory?.ThrowIfUnsafe(buffer.Length * 2);
                        var document = JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
                        outcome = "success";
                        return document;
                    }
                    finally { ArrayPool<byte>.Shared.Return(chunk); }
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    outcome = "timeout";
                    var message = $"Elastic Provider request timed out during {operation} while {phase}. " +
                        $"Configured per-request timeout: {_options.TimeoutSeconds} seconds (AutoRun.Provider.TimeoutSeconds); " +
                        $"response bytes read: {bytesRead}. Narrow the search or increase that timeout and prepare again.";
                    throw new ElasticProviderException(message, diagnostic:
                        Diagnostic("providerTimeout", message, operation, request.Method.Method, null, attempt, started,
                            bytesRead: bytesRead));
                }
                catch (Exception ex) when (ex is HttpRequestException or IOException)
                {
                    outcome = "transportError";
                    const string message = "Elastic Provider request failed.";
                    throw new ElasticProviderException(message, diagnostic:
                        Diagnostic("providerTransportError", message, operation, request.Method.Method, null, attempt, started,
                            bytesRead: bytesRead));
                }
                catch (JsonException)
                {
                    outcome = "invalidJson";
                    const string message = "Elastic Provider returned invalid JSON.";
                    throw new ElasticProviderException(message, diagnostic:
                        Diagnostic("providerInvalidJson", message, operation, request.Method.Method, null, attempt, started,
                            bytesRead: bytesRead));
                }
                finally
                {
                    _lastAttemptFinished = _time.GetTimestamp();
                    if (cancellationToken.IsCancellationRequested) outcome = "cancelled";
                    var elapsed = _time.GetElapsedTime(attemptStarted).TotalMilliseconds;
                    var tags = new TagList
                    {
                        { "provider.operation", operation }, { "http.request.method", request.Method.Method },
                        { "http.response.status_code", statusCode }, { "outcome", outcome }
                    };
                    RequestsMetric.Add(1, tags);
                    if (attempt > 1) RetriesMetric.Add(1, tags);
                    DurationMetric.Record(elapsed, tags);
                    logger?.Log(operation == "sensor type lookup" ? LogLevel.Debug : LogLevel.Information,
                        "Elastic Provider {Operation} attempt {Attempt} of {MaxAttempts} finished with {Outcome}, HTTP {StatusCode}, {ResponseBytes} bytes in {ElapsedMs} ms.",
                        operation, attempt, _options.MaxRetries + 1, outcome, statusCode, bytesRead, elapsed);
                }
            }
            throw new InvalidOperationException("Provider retry loop completed without a result.");
        }
        finally { _sendGate.Release(); }
    }

    private HttpRequestMessage CloneRequest(HttpRequestMessage original, byte[]? body)
    {
        var request = new HttpRequestMessage(original.Method, original.RequestUri)
        {
            Version = original.Version,
            VersionPolicy = original.VersionPolicy
        };
        if (body is not null)
        {
            request.Content = new ByteArrayContent(body);
            foreach (var header in original.Content!.Headers)
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        foreach (var (name, value) in _headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Dispose();
                throw new ElasticProviderException(ElasticProviderHeaders.ValidationError);
            }
        return request;
    }

    private void ValidateTransportOptions()
    {
        if (_options.TimeoutSeconds <= 0)
            throw new ElasticProviderException("Provider timeout must be positive.");
        if (_options.MaxResponseBytes is <= 0 or > 536_870_912)
            throw new ElasticProviderException("Provider MaxResponseBytes must be between 1 and 536870912 bytes.");
        if (_options.RequestDelayMs is < 0 or > 60_000 || _options.MaxRetries is < 0 or > 5 ||
            _options.RetryDelayMs is < 1 or > 60_000)
            throw new ElasticProviderException("Provider RequestDelayMs must be 0 to 60000, MaxRetries 0 to 5, and RetryDelayMs 1 to 60000.");
    }

    private TimeSpan RetryAfter(HttpResponseMessage response)
    {
        // An oversized numeric value may be rejected by the typed header parser. It still
        // explicitly asks us not to retry within our bounded wait, so do not ignore it.
        if (response.Headers.TryGetValues("Retry-After", out var values))
        {
            var supplied = values.Take(2).ToArray();
            if (supplied.Length != 1) return TimeSpan.Zero;
            var raw = supplied[0].Trim();
            if (raw is { Length: > 0 } && raw.All(c => c is >= '0' and <= '9'))
            {
                var significant = raw.TrimStart('0');
                if (significant.Length > 2 || significant.Length == 2 && string.CompareOrdinal(significant, "60") > 0)
                    return MaximumRetryDelay + TimeSpan.FromSeconds(1);
            }
        }
        var header = response.Headers.RetryAfter;
        var delay = header?.Delta ?? (header?.Date is { } date ? date - _time.GetUtcNow() : TimeSpan.Zero);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private async Task<ErrorBody> ReadErrorBodyAsync(HttpResponseMessage response, Uri requestUri,
        CancellationToken timeout, CancellationToken caller)
    {
        var buffer = new byte[Math.Min(ErrorBodyLimit, _options.MaxResponseBytes)];
        var count = 0;
        string? unavailable = null;
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(timeout);
            int read;
            while (count < buffer.Length && (read = await stream.ReadAsync(buffer.AsMemory(count), timeout)) != 0)
                count += read;
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        { unavailable = "Reading the provider error body timed out."; }
        catch (Exception ex) when (ex is IOException or HttpRequestException or InvalidOperationException or NotSupportedException)
        { unavailable = "The provider error body could not be read."; }
        caller.ThrowIfCancellationRequested();
        return new(response.Content.Headers.ContentType?.MediaType,
            count == 0 ? null : ProviderErrorSanitizer.Sanitize(buffer.AsSpan(0, count), requestUri, _headers.Values),
            count == buffer.Length || response.Content.Headers.ContentLength > count, unavailable, count);
    }

    private AutoRunDiagnostic Diagnostic(string code, string message, string operation, string method,
        int? status, int attempts, long started, ErrorBody? body = null, long bytesRead = 0) =>
        new(_time.GetUtcNow(), "error", code, "provider", message,
            Upstream: new(operation, method, status, attempts, _time.GetElapsedTime(started).TotalMilliseconds,
                body?.ContentType, body?.Excerpt, body?.Truncated ?? false, body?.Unavailable, bytesRead));

    private sealed record ErrorBody(string? ContentType, string? Excerpt, bool Truncated, string? Unavailable, long BytesRead);
}
