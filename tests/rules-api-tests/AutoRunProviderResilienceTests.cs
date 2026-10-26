using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderResilienceTests
{
    [Theory]
    [InlineData(429)]
    [InlineData(503)]
    public async Task Transient_status_retries_identical_fresh_requests_and_disposes_responses_before_wait(int status)
    {
        var time = new TestClock();
        var requests = new List<(HttpRequestMessage Request, string Uri, string Body, string ContentType, string Auth)>();
        var bodies = new List<TrackedStream>();
        var progress = new List<ProviderProgress>();
        using var client = new HttpClient(new Handler(async (request, _) =>
        {
            requests.Add((request, request.RequestUri!.AbsoluteUri, await request.Content!.ReadAsStringAsync(),
                request.Content.Headers.ContentType!.ToString(), request.Headers.GetValues("Authorization").Single()));
            if (requests.Count == 3) return Json(new { entities_list = Array.Empty<object>() });
            var body = new TrackedStream(Encoding.UTF8.GetBytes("Busy"));
            bodies.Add(body);
            return new((HttpStatusCode)status) { Content = new StreamContent(body) };
        }));
        var options = Config();
        options.Headers["Authorization"] = "Bearer credential-secret";
        var provider = new ElasticOverlayProvider(client, Options.Create(options), timeProvider: time);
        var query = JsonSerializer.SerializeToElement(new { filter = new { name = "same-query" } });
        var pending = provider.SearchPageAsync(query, "/next?searchAfter=%5B1%2C%22a%2Bb%22%5D", CancellationToken.None, progress.Add);

        await time.WaitForTimerAsync(TimeSpan.FromSeconds(2));
        Assert.Single(requests);
        Assert.True(bodies[0].Disposed);
        Assert.Equal("retryWait", progress[^1].Phase);
        Assert.Equal(2, progress[^1].Attempt);
        time.Advance(TimeSpan.FromSeconds(2));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(4));
        Assert.Equal(2, requests.Count);
        Assert.True(bodies[1].Disposed);
        time.Advance(TimeSpan.FromSeconds(4));
        Assert.Empty((await pending.WaitAsync(TimeSpan.FromSeconds(5))).Items);

        Assert.Equal(3, requests.Count);
        Assert.Equal(3, requests.Select(r => r.Request).Distinct().Count());
        Assert.Single(requests.Select(r => (r.Uri, r.Body, r.ContentType, r.Auth)).Distinct());
        Assert.Contains("searchAfter=%5B1%2C%22a%2Bb%22%5D", requests[0].Uri);
        Assert.Equal(query.GetRawText(), requests[0].Body);
    }

    [Fact]
    public async Task First_request_is_immediate_subsequent_requests_are_serial_and_paced()
    {
        var time = new TestClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()), timeProvider: time);
        await provider.GetAsync("one", CancellationToken.None);
        Assert.Equal(1, calls);
        var second = provider.GetAsync("two", CancellationToken.None);
        var third = provider.GetAsync("three", CancellationToken.None);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(1, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await second.WaitAsync(TimeSpan.FromSeconds(5));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(2, calls);
        time.Advance(TimeSpan.FromSeconds(1));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(3, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_interrupts_pacing_or_retry_without_another_request(bool retry)
    {
        var time = new TestClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(retry ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound));
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()), timeProvider: time);
        if (!retry) await provider.GetAsync("first", CancellationToken.None);
        using var cancellation = new CancellationTokenSource();
        var pending = provider.GetAsync("next", cancellation.Token);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(retry ? 2 : 1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("delta")]
    [InlineData("date")]
    [InlineData("invalid")]
    [InlineData("past")]
    public async Task RetryAfter_is_combined_with_backoff_and_pacing(string kind)
    {
        var time = new TestClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.NotFound);
            if (kind == "delta") response.Headers.RetryAfter = new(TimeSpan.FromSeconds(5));
            if (kind == "date") response.Headers.RetryAfter = new(time.GetUtcNow().AddSeconds(5));
            if (kind == "past") response.Headers.RetryAfter = new(time.GetUtcNow().AddMinutes(-1));
            if (kind == "invalid") response.Headers.TryAddWithoutValidation("Retry-After", "not-a-date");
            return Task.FromResult(response);
        }));
        var options = Config(); options.RequestDelayMs = 3000;
        var provider = new ElasticOverlayProvider(client, Options.Create(options), timeProvider: time);
        var pending = provider.GetAsync("one", CancellationToken.None);
        var expected = TimeSpan.FromSeconds(kind is "delta" or "date" ? 5 : 3);
        await time.WaitForTimerAsync(expected);
        Assert.Equal(1, calls);
        time.Advance(expected);
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("61")]
    [InlineData("999999999999999999999999")]
    public async Task Excessive_RetryAfter_stops_without_retrying_early(string retryAfter)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
            response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);
            return Task.FromResult(response);
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetAsync("one", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Contains("60-second", error.Message);
        Assert.Equal(503, error.Diagnostic!.Upstream!.StatusCode);
        Assert.Equal(1, error.Diagnostic.Upstream.Attempts);
    }

    [Fact]
    public async Task Exhaustion_keeps_only_bounded_sanitized_diagnostic_and_exact_attempt_count()
    {
        var time = new TestClock();
        var calls = 0;
        var raw = "{\"message\":\"temporarily busy credential-secret https://provider.example/private?searchAfter=cursor-secret\", " +
            "\"authorization\":\"Bearer another-secret\",\"searchAfter\":\"cursor-secret\",\"padding\":\"" + new string('x', 12000) + "\"}";
        var streams = new List<TrackedStream>();
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            var stream = new TrackedStream(Encoding.UTF8.GetBytes(raw));
            streams.Add(stream);
            var content = new StreamContent(stream);
            content.Headers.ContentType = new("application/json");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = content });
        }));
        var options = Config(); options.Headers["Authorization"] = "Bearer credential-secret";
        var provider = new ElasticOverlayProvider(client, Options.Create(options), timeProvider: time);
        var pending = provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { }),
            "/next?searchAfter=cursor-secret", CancellationToken.None);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(2)); time.Advance(TimeSpan.FromSeconds(2));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(4)); time.Advance(TimeSpan.FromSeconds(4));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        var details = error.Diagnostic!.Upstream!;
        Assert.Equal(3, calls);
        Assert.Equal(3, details.Attempts);
        Assert.Equal(503, details.StatusCode);
        Assert.Equal(8192, details.BytesRead);
        Assert.True(details.BodyTruncated);
        Assert.Equal("application/json", details.ContentType);
        Assert.InRange(details.BodyExcerpt!.Length, 1, 8192);
        Assert.Contains("temporarily busy", details.BodyExcerpt);
        foreach (var secret in new[] { "credential-secret", "another-secret", "cursor-secret", "provider.example" })
            Assert.DoesNotContain(secret, JsonSerializer.Serialize(error.Diagnostic));
        Assert.All(streams, stream => { Assert.True(stream.Disposed); Assert.Equal(8192, stream.TotalRead); });
    }

    [Theory]
    [InlineData(400)]
    [InlineData(401)]
    [InlineData(408)]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(504)]
    public async Task Other_statuses_fail_once_with_their_original_status(int status)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("provider detail") });
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var error = await Assert.ThrowsAsync<ElasticProviderException>(() => provider.GetAsync("one", CancellationToken.None));
        Assert.Equal(status, error.Diagnostic!.Upstream!.StatusCode);
        Assert.Equal(1, calls);
        Assert.Equal("provider detail", error.Diagnostic.Upstream.BodyExcerpt);
    }

    [Fact]
    public async Task Broken_error_body_does_not_replace_the_http_status()
    {
        using var stream = new BrokenStream();
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadGateway)
        { Content = new StreamContent(stream) })));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var error = await Assert.ThrowsAsync<ElasticProviderException>(() => provider.GetAsync("one", CancellationToken.None));
        Assert.Equal(502, error.Diagnostic!.Upstream!.StatusCode);
        Assert.NotNull(error.Diagnostic.Upstream.BodyUnavailableReason);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task Zero_retries_means_one_request_and_a_typed_unavailable_failure()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests));
        }));
        var options = Config(); options.MaxRetries = 0;
        var provider = new ElasticOverlayProvider(client, Options.Create(options));
        var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => provider.GetAsync("one", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal(429, error.Diagnostic!.Upstream!.StatusCode);
    }

    [Fact]
    public async Task Retry_backoff_is_capped_at_sixty_seconds()
    {
        var time = new TestClock();
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        }));
        var options = Config(); options.RetryDelayMs = 40_000;
        var provider = new ElasticOverlayProvider(client, Options.Create(options), timeProvider: time);
        var pending = provider.GetAsync("one", CancellationToken.None);
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(40)); time.Advance(TimeSpan.FromSeconds(40));
        await time.WaitForTimerAsync(TimeSpan.FromSeconds(60)); time.Advance(TimeSpan.FromSeconds(60));
        await Assert.ThrowsAsync<ProviderUnavailableException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task Error_body_timeout_preserves_status_and_cancellation_still_cancels_the_request()
    {
        foreach (var cancelCaller in new[] { false, true })
        {
            var time = new TestClock();
            using var stream = new StalledStream();
            using var client = new HttpClient(new Handler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
            { Content = new StreamContent(stream) })));
            var options = Config(); options.MaxRetries = 0;
            var provider = new ElasticOverlayProvider(client, Options.Create(options), timeProvider: time);
            using var cancellation = new CancellationTokenSource();
            var pending = provider.GetAsync("one", cancellation.Token);
            await stream.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (cancelCaller)
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                time.Advance(TimeSpan.FromSeconds(30));
                var error = await Assert.ThrowsAsync<ProviderUnavailableException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(503, error.Diagnostic!.Upstream!.StatusCode);
                Assert.Contains("timed out", error.Diagnostic.Upstream.BodyUnavailableReason);
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Transport_failures_are_not_retried_and_do_not_expose_inner_exception_text(bool bodyFailure)
    {
        var calls = 0;
        using var client = new HttpClient(new Handler((_, _) =>
        {
            calls++;
            if (!bodyFailure) throw new HttpRequestException("private-token https://provider.example/private");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenStream()) });
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var error = await Assert.ThrowsAsync<ElasticProviderException>(() => provider.GetAsync("one", CancellationToken.None));
        Assert.Equal(1, calls);
        Assert.Equal("providerTransportError", error.Diagnostic!.Code);
        Assert.DoesNotContain("private", error.ToString());
        Assert.Null(error.InnerException);
    }

    private static ElasticProviderOptions Config() => new() { BaseUrl = "https://provider.example" };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request, cancellationToken);
    }

    private sealed class TrackedStream(byte[] data) : MemoryStream(data)
    {
        public bool Disposed { get; private set; }
        public int TotalRead { get; private set; }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = base.Read(buffer.Span);
            TotalRead += read;
            return ValueTask.FromResult(read);
        }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
    }

    private sealed class BrokenStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("private response URL and token must not escape");
    }

    private sealed class StalledStream : MemoryStream
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly object _gate = new();
        private readonly List<TestTimer> _timers = [];
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() { lock (_gate) return _ticks; }
        public override DateTimeOffset GetUtcNow() => new DateTimeOffset(2026, 10, 26, 12, 0, 0, TimeSpan.Zero).AddTicks(GetTimestamp());
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new TestTimer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_gate) _timers.Add(timer);
            _created.Writer.TryWrite(dueTime);
            return timer;
        }
        public async Task WaitForTimerAsync(TimeSpan expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var due = await _created.Reader.ReadAsync(timeout.Token);
                if (due == expected) return;
            }
        }
        public void Advance(TimeSpan by)
        {
            TestTimer[] due;
            lock (_gate)
            {
                _ticks += by.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToArray();
                foreach (var timer in due) timer.Disposed = true;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class TestTimer(TestClock owner, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; private set; }
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner._gate)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : owner._ticks + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (owner._gate) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
