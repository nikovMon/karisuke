using System.Net;
using System.Text;
using System.Threading.Channels;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class SensorNameCatalogTests
{
    private const string Secret = "catalog-test-secret";
    private static readonly TimeSpan Interval = TimeSpan.FromDays(1);

    [Fact]
    public async Task Concurrent_first_loads_share_one_request_and_cache_exact_names()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (request, token) =>
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("https://sensors.example/sensors/api/v1/sensors/list/sensor/", request.RequestUri!.AbsoluteUri);
            Assert.False(request.Headers.Contains("Authorization"));
            await release.Task.WaitAsync(token);
            return Json("[{\"sensorName\":\"aRe\"},{\"sensorName\":\"aRe\"},{\"sensorName\":\"Other\"}]");
        });

        var reads = Enumerable.Range(0, 12).Select(_ => fixture.Catalog.GetSnapshotAsync(CancellationToken.None)).ToArray();
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, fixture.Handler.Calls);
        release.SetResult();
        var snapshots = await Task.WhenAll(reads).WaitAsync(TimeSpan.FromSeconds(5));

        var snapshot = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string>>(snapshots[0]);
        Assert.Equal(2, snapshot.Count);
        Assert.Equal("aRe", snapshot["ARE"]);
        Assert.All(snapshots, item => Assert.Same(snapshot, item));
        Assert.Same(snapshot, await fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Cancelling_one_waiter_does_not_cancel_another_first_load()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, token) =>
        {
            await release.Task.WaitAsync(token);
            return Json("[{\"sensorName\":\"aRe\"}]");
        });
        using var cancellation = new CancellationTokenSource();
        var cancelled = fixture.Catalog.GetSnapshotAsync(cancellation.Token);
        var surviving = fixture.Catalog.GetSnapshotAsync(CancellationToken.None);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(5)));
        release.SetResult();
        Assert.Equal("aRe", (await surviving.WaitAsync(TimeSpan.FromSeconds(5)))!["are"]);
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task Hosted_refresh_replaces_snapshot_without_mutating_readers_and_retains_it_on_failure()
    {
        var clock = new TestClock();
        var responses = new Queue<HttpResponseMessage>([
            Json("[{\"sensorName\":\"aRe\"},{\"sensorName\":\"Removed\"}]"),
            Json("[{\"sensorName\":\"ARE\"},{\"sensorName\":\"Added\"}]"),
            new(HttpStatusCode.ServiceUnavailable)]);
        using var fixture = new Fixture((_, _) => Task.FromResult(responses.Dequeue()), time: clock);
        await fixture.Catalog.StartAsync(CancellationToken.None);
        await clock.WaitForTimerAsync(Interval);
        var first = (await fixture.Catalog.GetSnapshotAsync(CancellationToken.None))!;

        clock.Advance(Interval);
        await clock.WaitForTimerAsync(Interval);
        var second = (await fixture.Catalog.GetSnapshotAsync(CancellationToken.None))!;
        Assert.NotSame(first, second);
        Assert.Equal("ARE", second["aRe"]);
        Assert.True(second.ContainsKey("Added"));
        Assert.False(second.ContainsKey("Removed"));
        Assert.Equal("aRe", first["are"]);
        Assert.True(first.ContainsKey("Removed"));

        clock.Advance(Interval);
        await clock.WaitForTimerAsync(Interval);
        Assert.Same(second, await fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
        Assert.Equal(3, fixture.Handler.Calls);
        await fixture.Catalog.StopAsync(CancellationToken.None);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{}]")]
    [InlineData("[{\"sensorName\":null}]")]
    [InlineData("[{\"sensorName\":123}]")]
    [InlineData("[{\"sensorName\":\"\"}]")]
    [InlineData("[{\"sensorName\":\" aRe \"}]")]
    [InlineData("[{\"sensorName\":\"a\\nRe\"}]")]
    [InlineData("[{\"sensorName\":\"aRe\"},{\"sensorName\":\"ARE\"}]")]
    [InlineData("invalid json")]
    public async Task Malformed_or_ambiguous_first_response_fails_without_a_partial_snapshot(string body)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(body)));
        await Assert.ThrowsAsync<ElasticProviderException>(() => fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Initial_http_failure_is_sanitized_and_next_attempt_can_recover()
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(++calls == 1
            ? new HttpResponseMessage(HttpStatusCode.BadGateway) { Content = new StringContent(Secret) }
            : Json("[{\"sensorName\":\"Recovered\"}]")));
        var error = await Assert.ThrowsAsync<ElasticProviderException>(() => fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("sensors.example", error.ToString(), StringComparison.Ordinal);
        Assert.Equal("Recovered", (await fixture.Catalog.GetSnapshotAsync(CancellationToken.None))!["recovered"]);
    }

    [Theory]
    [InlineData(false, "https://sensors.example")]
    [InlineData(true, "")]
    public async Task Disabled_catalog_does_not_send_requests(bool enabled, string baseUrl)
    {
        using var fixture = new Fixture((_, _) => throw new InvalidOperationException("Unexpected request"),
            new() { BaseUrl = baseUrl }, enabled: enabled);
        await fixture.Catalog.StartAsync(CancellationToken.None);
        Assert.Null(await fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
        await fixture.Catalog.StopAsync(CancellationToken.None);
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Timeout_and_host_shutdown_cancel_inflight_http(bool shutdown)
    {
        var clock = new TestClock();
        var requestCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { requestCancelled.TrySetResult(); }
            return Json("[]");
        }, time: clock);
        var read = fixture.Catalog.GetSnapshotAsync(CancellationToken.None);
        await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown) await fixture.Catalog.StopAsync(CancellationToken.None);
        else
        {
            await clock.WaitForTimerAsync(TimeSpan.FromSeconds(30));
            clock.Advance(TimeSpan.FromSeconds(30));
        }
        await requestCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (shutdown) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        else await Assert.ThrowsAsync<ElasticProviderException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Oversized_response_is_rejected()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(new string(' ', SensorCatalogOptions.MaxResponseBytes + 1))));
        await Assert.ThrowsAsync<ElasticProviderException>(() => fixture.Catalog.GetSnapshotAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_stop_and_dispose_allow_repeated_host_shutdown(bool enabled)
    {
        using var fixture = new Fixture(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        }, enabled: enabled);
        await fixture.Catalog.StartAsync(CancellationToken.None);
        var read = fixture.Catalog.GetSnapshotAsync(CancellationToken.None);
        if (enabled) await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(Task.Run(() => fixture.Catalog.Dispose()),
            Task.Run(() => fixture.Catalog.StopAsync(CancellationToken.None))).WaitAsync(TimeSpan.FromSeconds(5));
        fixture.Catalog.Dispose();
        await fixture.Catalog.StopAsync(CancellationToken.None);

        if (enabled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(TimeSpan.FromSeconds(5)));
        else Assert.Null(await read);
    }

    private static HttpResponseMessage Json(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };

    private sealed class Fixture : IDisposable, IHttpClientFactory
    {
        private readonly List<HttpClient> _clients = [];
        public Handler Handler { get; }
        public SensorNameCatalog Catalog { get; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response,
            SensorCatalogOptions? options = null, TimeProvider? time = null, bool enabled = true)
        {
            Handler = new(response);
            Catalog = new(this, Options.Create(options ?? new() { BaseUrl = "https://sensors.example" }),
                Options.Create(new AutoRunOptions { Enabled = enabled }), time ?? TimeProvider.System,
                NullLogger<SensorNameCatalog>.Instance);
        }
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(SensorNameCatalog.HttpClientName, name);
            var client = new HttpClient(Handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
            _clients.Add(client);
            return client;
        }
        public void Dispose()
        {
            Catalog.Dispose();
            foreach (var client in _clients) client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Interlocked.Increment(ref _calls);
            Entered.TrySetResult();
            return response(request, token);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private readonly List<Timer> _timers = [];
        private readonly Channel<TimeSpan> _created = Channel.CreateUnbounded<TimeSpan>();
        private long _ticks;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new Timer(this, callback, state);
            timer.Change(dueTime, period);
            lock (_timers) _timers.Add(timer);
            _created.Writer.TryWrite(dueTime);
            return timer;
        }
        public async Task WaitForTimerAsync(TimeSpan expected)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (await _created.Reader.ReadAsync(timeout.Token) != expected) { }
        }
        public void Advance(TimeSpan elapsed)
        {
            Timer[] due;
            lock (_timers)
            {
                _ticks += elapsed.Ticks;
                due = _timers.Where(timer => !timer.Disposed && timer.Due <= _ticks).ToArray();
                foreach (var timer in due) timer.Disposed = true;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        private sealed class Timer(TestClock clock, TimerCallback callback, object? state) : ITimer
        {
            public TimerCallback Callback { get; } = callback;
            public object? State { get; } = state;
            public long Due { get; private set; }
            public bool Disposed { get; set; }
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (clock._timers)
                {
                    if (Disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : clock._ticks + dueTime.Ticks;
                    return true;
                }
            }
            public void Dispose() { lock (clock._timers) Disposed = true; }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
