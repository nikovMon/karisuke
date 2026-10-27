using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunConcurrencyTests
{
    [Fact]
    public async Task Three_preparations_and_three_publications_run_together_with_four_global_sends()
    {
        await using var fixture = await Fixture.StartAsync();
        var ready = new List<AutoRunSummary>();
        for (var i = 0; i < 3; i++) ready.Add(await fixture.ReadyAsync($"publish-{i}-a", $"publish-{i}-b"));
        var publishGate = Gate();
        fixture.Publisher.Handler = async (_, _, token) => { await publishGate.Task.WaitAsync(token); return new(AutoRunItemOutcome.Confirmed); };
        foreach (var job in ready) fixture.Service.Start(job.Id);
        await UntilAsync(() => fixture.Publisher.Active == 4 && fixture.Service.Execution().ActivePublications == 3);

        var prepareGate = Gate();
        fixture.Provider.Handler = async (id, token) => { await prepareGate.Task.WaitAsync(token); return Result(id); };
        var preparing = new List<AutoRunSummary>();
        for (var i = 0; i < 4; i++) preparing.Add(await fixture.PreviewAsync($"prepare-{i}"));
        await UntilAsync(() => fixture.Provider.Active == 3);
        var execution = fixture.Service.Execution();
        Assert.Equal(3, execution.ActivePreparations);
        Assert.Equal(3, execution.ActivePublications);
        Assert.Equal(1, execution.QueuedPreparations);
        Assert.Equal(4, execution.ActivePublicationPermits);
        Assert.Equal(AutoRunState.QueuedPreparation, fixture.Service.Get(preparing[3].Id).State);

        prepareGate.SetResult();
        foreach (var job in preparing) await fixture.WaitAsync(job.Id, AutoRunState.Ready);
        Assert.Equal(4, fixture.Publisher.Active);
        publishGate.SetResult();
        foreach (var job in ready) await fixture.WaitAsync(job.Id, AutoRunState.Completed);
        Assert.Equal(3, fixture.Provider.Maximum);
        Assert.Equal(4, fixture.Publisher.Maximum);
    }

    [Fact]
    public async Task Preparation_and_publication_queues_each_follow_FIFO_order()
    {
        await using var fixture = await Fixture.StartAsync(preparations: 1, publications: 1);
        var providerGates = new Dictionary<string, TaskCompletionSource> { ["a"] = Gate(), ["b"] = Gate(), ["c"] = Gate() };
        fixture.Provider.Handler = async (id, token) => { await providerGates[id].Task.WaitAsync(token); return Result(id); };
        var a = await fixture.PreviewAsync("a");
        var b = await fixture.PreviewAsync("b");
        var c = await fixture.PreviewAsync("c");
        await UntilAsync(() => fixture.Provider.Calls.Count == 1);
        Assert.Equal(1, fixture.Service.Get(b.Id).QueuePosition);
        Assert.Equal(2, fixture.Service.Get(c.Id).QueuePosition);
        providerGates["a"].SetResult();
        await fixture.WaitAsync(a.Id, AutoRunState.Ready);
        await UntilAsync(() => fixture.Provider.Calls.Count == 2);
        Assert.Equal(new[] { "a", "b" }, fixture.Provider.Calls.ToArray());
        providerGates["b"].SetResult();
        await UntilAsync(() => fixture.Provider.Calls.Count == 3);
        providerGates["c"].SetResult();
        await fixture.WaitAsync(b.Id, AutoRunState.Ready);
        await fixture.WaitAsync(c.Id, AutoRunState.Ready);

        var publisherGates = new Dictionary<string, TaskCompletionSource> { [a.Id] = Gate(), [b.Id] = Gate(), [c.Id] = Gate() };
        fixture.Publisher.Handler = async (run, _, token) => { await publisherGates[run].Task.WaitAsync(token); return new(AutoRunItemOutcome.Confirmed); };
        fixture.Service.Start(a.Id);
        fixture.Service.Start(b.Id);
        fixture.Service.Start(c.Id);
        await UntilAsync(() => fixture.Publisher.Calls.Count == 1);
        Assert.Equal(1, fixture.Service.Get(b.Id).QueuePosition);
        Assert.Equal(2, fixture.Service.Get(c.Id).QueuePosition);
        publisherGates[a.Id].SetResult();
        await UntilAsync(() => fixture.Publisher.Calls.Count == 2);
        Assert.Equal(new[] { a.Id, b.Id }, fixture.Publisher.Calls.Select(call => call.Run).ToArray());
        publisherGates[b.Id].SetResult();
        await UntilAsync(() => fixture.Publisher.Calls.Count == 3);
        publisherGates[c.Id].SetResult();
        await fixture.WaitAsync(c.Id, AutoRunState.Completed);
    }

    [Fact]
    public async Task Waiting_queue_limit_rejects_excess_and_cancel_delete_release_positions_without_requests()
    {
        await using var fixture = await Fixture.StartAsync();
        var providerGate = Gate();
        fixture.Provider.Handler = async (id, token) => { await providerGate.Task.WaitAsync(token); return Result(id); };
        for (var i = 0; i < 3; i++) await fixture.PreviewAsync($"active-{i}");
        await UntilAsync(() => fixture.Provider.Active == 3);
        var queued = new List<AutoRunSummary>();
        for (var i = 0; i < 20; i++) queued.Add(await fixture.PreviewAsync($"queued-{i}"));
        var error = await Assert.ThrowsAsync<AutoRunRequestException>(() => fixture.PreviewAsync("too-many"));
        Assert.Equal(409, error.StatusCode);
        Assert.Equal("auto_run_queue_full", error.Code);
        Assert.Equal(23, fixture.Service.ListSummaries().Count);
        Assert.Equal(20, fixture.Service.Execution().QueuedPreparations);

        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Cancel(queued[0].Id).State);
        var deletion = fixture.Service.Delete(queued[1].Id);
        Assert.Equal(new[] { queued[1].Id }, deletion.DeletedIds);
        Assert.Empty(deletion.PendingIds);
        Assert.True(deletion.ReleasedRetainedBytes > 0);
        Assert.Equal(1, fixture.Service.Get(queued[2].Id).QueuePosition);
        await fixture.PreviewAsync("replacement-1");
        await fixture.PreviewAsync("replacement-2");
        Assert.Equal(20, fixture.Service.Execution().QueuedPreparations);
        Assert.Equal(3, fixture.Provider.Calls.Count);
        fixture.Service.DeleteAll();
        await UntilAsync(() => fixture.Service.ListSummaries().Count == 0);
        Assert.Equal(0, fixture.Resources.ReservedPreparationBytes);
        Assert.Equal(0, fixture.Service.Execution().RetainedBytes);
        Assert.Equal(3, fixture.Provider.Calls.Count);
    }

    [Fact]
    public async Task Queued_start_is_idempotent_and_outlives_ready_expiry_until_capacity_is_available()
    {
        var time = new Clock();
        await using var fixture = await Fixture.StartAsync(publications: 1, time: time);
        var a = await fixture.ReadyAsync("a");
        var b = await fixture.ReadyAsync("b");
        var untouched = await fixture.ReadyAsync("untouched");
        var publisherGate = Gate();
        fixture.Publisher.Handler = async (_, _, token) => { await publisherGate.Task.WaitAsync(token); return new(AutoRunItemOutcome.Confirmed); };
        fixture.Service.Start(a.Id);
        await UntilAsync(() => fixture.Publisher.Active == 1);
        var started = fixture.Service.Start(b.Id);
        Assert.Equal(AutoRunState.QueuedPublication, started.State);
        Assert.Null(started.StartedAt);
        Assert.NotNull(started.StartRequestedAt);
        Assert.Equal(started.StartRequestedAt, fixture.Service.Start(b.Id).StartRequestedAt);
        Assert.Equal(1, fixture.Service.Execution().QueuedPublications);

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(AutoRunState.QueuedPublication, fixture.Service.Get(b.Id).State);
        Assert.Null(fixture.Service.Get(b.Id).ExpiresAt);
        Assert.Equal(AutoRunState.Expired, fixture.Service.Get(untouched.Id).State);
        publisherGate.SetResult();
        var completed = await fixture.WaitAsync(b.Id, AutoRunState.Completed);
        Assert.True(completed.StartedAt > completed.StartRequestedAt);
        Assert.Single(fixture.Publisher.Calls, call => call.Run == b.Id);
        Assert.Equal(AutoRunState.Completed, fixture.Service.Start(b.Id).State);
    }

    [Fact]
    public async Task Preparation_waiting_for_memory_uses_no_worker_and_resumes_in_order()
    {
        var memory = new Memory { Headroom = 2 * 1024 * 1024 };
        await using var fixture = await Fixture.StartAsync(memory: memory);
        var waiting = await fixture.PreviewAsync("waiting");
        Assert.Equal(AutoRunState.QueuedPreparation, waiting.State);
        await UntilAsync(() => fixture.Service.Get(waiting.Id).WaitReason == "memory");
        Assert.Equal(0, fixture.Service.Execution().ActivePreparations);
        Assert.Equal(0, fixture.Resources.ReservedPreparationBytes);
        Assert.Empty(fixture.Provider.Calls);

        memory.Headroom = 30 * 1024 * 1024;
        // A new admission immediately rechecks existing FIFO work, without waiting for housekeeping.
        var next = await fixture.PreviewAsync("next");
        await fixture.WaitAsync(waiting.Id, AutoRunState.Ready);
        await fixture.WaitAsync(next.Id, AutoRunState.Ready);
        await UntilAsync(() => fixture.Resources.ReservedPreparationBytes == 0);
        Assert.Equal(2, fixture.Provider.Calls.Count);
    }

    [Fact]
    public async Task Shutdown_cancels_queued_work_and_waits_for_running_cleanup_before_returning_leases()
    {
        await using var fixture = await Fixture.StartAsync(preparations: 1, publications: 1);
        var a = await fixture.ReadyAsync("publish-a");
        var b = await fixture.ReadyAsync("publish-b");
        var cleanup = Gate();
        var prepCancelled = Gate();
        var publishCancelled = Gate();
        fixture.Publisher.Handler = async (_, _, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { publishCancelled.TrySetResult(); await cleanup.Task; throw; }
            return new(AutoRunItemOutcome.Confirmed);
        };
        fixture.Service.Start(a.Id);
        fixture.Service.Start(b.Id);
        await UntilAsync(() => fixture.Publisher.Active == 1);
        fixture.Provider.Handler = async (id, token) =>
        {
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            catch (OperationCanceledException) { prepCancelled.TrySetResult(); await cleanup.Task; throw; }
            return Result(id);
        };
        var preparing = await fixture.PreviewAsync("prepare-a");
        var queued = await fixture.PreviewAsync("prepare-b");
        await UntilAsync(() => fixture.Provider.Active == 1);

        var stopping = fixture.Service.StopAsync(CancellationToken.None);
        try
        {
            await Task.WhenAll(prepCancelled.Task, publishCancelled.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.Equal(503, (await Assert.ThrowsAsync<AutoRunRequestException>(() => fixture.PreviewAsync("after-stop"))).StatusCode);
            await UntilAsync(() => fixture.Service.Get(queued.Id).State == AutoRunState.Cancelled);
            Assert.Equal(AutoRunState.Cancelled, fixture.Service.Get(b.Id).State);
            Assert.True(fixture.Resources.ReservedPreparationBytes > 0);
            Assert.Equal(1, fixture.Resources.ActivePublicationPermits);
        }
        finally { cleanup.TrySetResult(); }
        await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Get(preparing.Id).State);
        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Get(a.Id).State);
        Assert.Equal(0, fixture.Resources.ReservedPreparationBytes);
        Assert.Equal(0, fixture.Resources.ActivePublicationPermits);
        Assert.Equal(0, fixture.Service.Execution().ActivePreparations);
        Assert.Equal(0, fixture.Service.Execution().ActivePublications);
    }

    [Fact]
    public async Task Failure_of_one_job_does_not_prevent_other_preparations_or_publications()
    {
        await using var fixture = await Fixture.StartAsync();
        fixture.Provider.Handler = (id, _) => id == "bad-prepare"
            ? Task.FromException<ProviderOverlayResult>(new InvalidOperationException("Controlled preparation failure."))
            : Task.FromResult(Result(id));
        var failed = await fixture.PreviewAsync("bad-prepare");
        var partial = await fixture.ReadyAsync("bad-send", "good-send");
        var good = await fixture.ReadyAsync("other-send");
        await fixture.WaitAsync(failed.Id, AutoRunState.Failed);
        fixture.Publisher.Handler = (_, id, _) => Task.FromResult(id == "bad-send"
            ? new AutoRunPublishResult(AutoRunItemOutcome.Failed, "Controlled broker rejection.")
            : new AutoRunPublishResult(AutoRunItemOutcome.Confirmed));
        fixture.Service.Start(partial.Id);
        fixture.Service.Start(good.Id);
        var errors = await fixture.WaitAsync(partial.Id, AutoRunState.CompletedWithErrors);
        var completed = await fixture.WaitAsync(good.Id, AutoRunState.Completed);
        Assert.Equal(1, errors.Counters.Failed);
        Assert.Equal(1, errors.Counters.Confirmed);
        Assert.Equal(1, completed.Counters.Confirmed);
        Assert.Equal(3, fixture.Publisher.Calls.Count);
    }

    [Fact]
    public async Task Cancellation_while_waiting_for_a_publisher_permit_keeps_the_item_unsent()
    {
        await using var fixture = await Fixture.StartAsync(channelPool: 1, timeoutSeconds: 1);
        var a = await fixture.ReadyAsync("active");
        var b = await fixture.ReadyAsync("waiting");
        var gate = Gate();
        fixture.Publisher.Handler = async (_, _, token) =>
        {
            // A broker client can still be unwinding after its timeout. It keeps the permit.
            await gate.Task;
            return token.IsCancellationRequested ? new(AutoRunItemOutcome.Uncertain, "Timed out") : new(AutoRunItemOutcome.Confirmed);
        };
        fixture.Service.Start(a.Id);
        await UntilAsync(() => fixture.Publisher.Active == 1);
        fixture.Service.Start(b.Id);
        try
        {
            await UntilAsync(() => fixture.Resources.WaitingPublicationPermits == 1);
            await Task.Delay(1100);
            var before = fixture.Service.Get(b.Id);
            Assert.Equal(0, before.Counters.InFlight);
            Assert.Equal(0, before.Counters.Uncertain);
            Assert.Equal(1, before.Counters.Remaining);
            fixture.Service.Cancel(b.Id);
            var cancelled = await fixture.WaitAsync(b.Id, AutoRunState.Cancelled);
            Assert.Equal(0, cancelled.Counters.Uncertain);
            Assert.Equal(1, cancelled.Counters.Remaining);
            Assert.Single(fixture.Publisher.Calls);
            Assert.Equal(AutoRunItemOutcome.Pending, Assert.Single(fixture.Service.Items(b.Id, 0, 10, null).Items).Outcome);
        }
        finally { gate.TrySetResult(); }
        await fixture.WaitAsync(a.Id, AutoRunState.CompletedWithErrors);
    }

    private static TaskCompletionSource Gate() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task UntilAsync(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > TimeSpan.FromSeconds(8)) throw new TimeoutException("Auto Run did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private static ProviderOverlayResult Result(string id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, sensorName = "camera", sensorType = "VIS", registrationQuality = "Accurate", bestResolution = 1,
            photoTime = "2026-10-20T10:00:00Z", imageUrl = "https://images.invalid/image", width = 100, height = 100,
            gridType = "grid", gridURI = "https://images.invalid/grid",
            roiFootprint = new { type = "Polygon", coordinates = new[] { new[] { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 1, 1 }, new[] { 0, 0 } } } }
        });
        return new(new(payload, GatewayInputMessageParser.Parse(payload), null, false, null), null);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public Provider Provider { get; } = new();
        public Publisher Publisher { get; } = new();
        public AutoRunService Service { get; }
        public AutoRunExecutionResources Resources { get; }

        private Fixture(int preparations, int publications, int channelPool, int timeoutSeconds, TimeProvider? time, Memory? memory)
        {
            var options = Options.Create(new AutoRunOptions
            {
                Enabled = true, MaxConcurrentPreparations = preparations, MaxConcurrentPublications = publications,
                Publication = new() { BatchSize = 10, DelayMs = 0 }, PublishTimeoutSeconds = timeoutSeconds,
                Destination = new() { Connection = new("test", "broker.invalid", 5672, "user", "pass", "/"), QueueName = "test" }
            });
            var providerOptions = Options.Create(new ElasticProviderOptions { MaxResponseBytes = 1024 * 1024 });
            Resources = new(options, providerOptions, Options.Create(new RabbitMqClientOptions { PublisherChannelPoolSize = channelPool }), memory);
            Service = new(new NoRules(), Provider, new ProviderQueryBuilder(providerOptions), Publisher, options,
                time ?? TimeProvider.System, NullLogger<AutoRunService>.Instance, memory, Resources);
        }

        public static async Task<Fixture> StartAsync(int preparations = 3, int publications = 3, int channelPool = 4,
            int timeoutSeconds = 30, TimeProvider? time = null, Memory? memory = null)
        {
            var fixture = new Fixture(preparations, publications, channelPool, timeoutSeconds, time, memory);
            await fixture.Service.StartAsync(default);
            return fixture;
        }

        public Task<AutoRunSummary> PreviewAsync(params string[] ids) => Service.PreviewAsync(new()
        {
            Source = new() { Mode = AutoRunSourceMode.OverlayIds, OverlayIds = ids }
        }, default);

        public async Task<AutoRunSummary> ReadyAsync(params string[] ids) => await WaitAsync((await PreviewAsync(ids)).Id, AutoRunState.Ready);

        public async Task<AutoRunSummary> WaitAsync(string id, AutoRunState state)
        {
            await UntilAsync(() => Service.Get(id).State == state);
            return Service.Get(id);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Service.StopAsync(timeout.Token);
            Service.Dispose();
        }
    }

    private sealed class Provider : IElasticOverlayProvider
    {
        private int _active, _maximum;
        public int Active => Volatile.Read(ref _active);
        public int Maximum => Volatile.Read(ref _maximum);
        public ConcurrentQueue<string> Calls { get; } = new();
        public Func<string, CancellationToken, Task<ProviderOverlayResult>>? Handler { get; set; }
        public async Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken, Action<ProviderProgress>? report = null)
        {
            Calls.Enqueue(id);
            Track(ref _active, ref _maximum);
            try { return Handler is null ? Result(id) : await Handler(id, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
        public Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken, Action<ProviderProgress>? report = null) => throw new NotSupportedException();
        public Task<ProviderOverlayResult> ResolveSensorTypeAsync(ProviderOverlay overlay, CancellationToken cancellationToken, Action<ProviderProgress>? report = null) => Task.FromResult(new ProviderOverlayResult(overlay, null));
    }

    private sealed class Publisher : IAutoRunPublisher
    {
        private int _active, _maximum;
        public int Active => Volatile.Read(ref _active);
        public int Maximum => Volatile.Read(ref _maximum);
        public ConcurrentQueue<(string Run, string Overlay)> Calls { get; } = new();
        public Func<string, string, CancellationToken, Task<AutoRunPublishResult>>? Handler { get; set; }
        public async Task<AutoRunPublishResult> PublishAsync(string runId, byte[] payload, CancellationToken cancellationToken)
        {
            using var document = JsonDocument.Parse(payload);
            var id = document.RootElement.GetProperty("id").GetString()!;
            Calls.Enqueue((runId, id));
            Track(ref _active, ref _maximum);
            try { return Handler is null ? new(AutoRunItemOutcome.Confirmed) : await Handler(runId, id, cancellationToken); }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private static void Track(ref int active, ref int maximum)
    {
        var count = Interlocked.Increment(ref active);
        int previous;
        do { previous = Volatile.Read(ref maximum); } while (count > previous && Interlocked.CompareExchange(ref maximum, count, previous) != previous);
    }

    private sealed class NoRules : IAutoRunRuleResolver
    {
        public Task<IReadOnlyList<AutoRunResolvedTarget>> ResolveAsync(IReadOnlyList<AutoRunTarget> targets, CancellationToken cancellationToken) => throw new InvalidOperationException("Direct requests should not resolve rules.");
    }

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    private sealed class Memory : IAutoRunMemoryGuard
    {
        public long Headroom { get; set; } = 100 * 1024 * 1024;
        public AutoRunMemorySnapshot Snapshot() => new(200 * 1024 * 1024, 100 * 1024 * 1024 - Headroom, 0,
            100 * 1024 * 1024, Headroom, Headroom <= 0);
        public bool CanAllocate(long additionalBytes = 0) => additionalBytes < Headroom;
        public void ThrowIfUnsafe(long additionalBytes = 0)
        {
            if (!CanAllocate(additionalBytes)) throw new AutoRunMemoryLimitException();
        }
    }
}
