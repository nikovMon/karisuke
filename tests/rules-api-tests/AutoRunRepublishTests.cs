using System.Text.Json;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed partial class AutoRunServiceTests
{
    [Theory]
    [InlineData(AutoRunSourceMode.Rules)]
    [InlineData(AutoRunSourceMode.Filters)]
    [InlineData(AutoRunSourceMode.OverlayIds)]
    public async Task RepublishReusesFrozenSelectionWithoutProviderOrRuleResolution(AutoRunSourceMode mode)
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one"), Overlay("two")]);
        var request = Request(["one", "two"], mode == AutoRunSourceMode.Filters ? [] : null) with
        {
            Source = new() { Mode = mode, OverlayIds = mode == AutoRunSourceMode.OverlayIds ? ["one", "two"] : null }
        };
        var ready = await fixture.ReadyAsync(request);
        var publication = new AutoRunPublication { BatchSize = 1, DelayMs = 1 };
        fixture.Service.UpdatePublication(ready.Id, publication);
        fixture.Service.Start(ready.Id);
        var original = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        var originalJson = JsonSerializer.Serialize(original with { Memory = null, ServerTime = default }, AutoRunJson.Options);
        var originalItems = fixture.Service.Items(ready.Id, 0, 10, null).Items;
        var calls = fixture.Publisher.Calls.ToArray();
        var providerCalls = (fixture.Provider.GetCalls.Count, fixture.Provider.SearchCursors.Count, fixture.Provider.SensorTypeCalls.Count);
        var ruleCalls = fixture.Resolver.Calls;
        fixture.Provider.Get = (_, _) => throw new InvalidOperationException("A publication rerun must not fetch overlays.");
        fixture.Provider.Search = (_, _, _) => throw new InvalidOperationException("A publication rerun must not search.");
        fixture.Provider.Resolve = (_, _) => throw new InvalidOperationException("A publication rerun must not resolve sensor types.");
        fixture.Resolver.Rules.Clear();

        var replay = fixture.Service.Republish(original.Id, CancellationToken.None);

        Assert.True(original.CanRepublish);
        Assert.NotEqual(original.Id, replay.Id);
        Assert.Equal(original.Id, replay.RepublishedFromRunId);
        Assert.Equal(AutoRunState.Ready, replay.State);
        Assert.False(replay.CanRepublish);
        Assert.Null(replay.PreparationStartedAt);
        Assert.Null(replay.StartRequestedAt);
        Assert.Null(replay.StartedAt);
        Assert.Equal(publication, replay.Publication);
        Assert.Equal(original.Counters.Selected, replay.Counters.Selected);
        Assert.Equal(0, replay.Counters.Confirmed + replay.Counters.Failed + replay.Counters.Uncertain + replay.Counters.InFlight);
        Assert.Equal(replay.Counters.Selected, replay.Counters.Remaining);
        Assert.Equal(original.Queries.Select(query => query.GetRawText()), replay.Queries.Select(query => query.GetRawText()));
        Assert.Equal(JsonSerializer.Serialize(original.Targets.Select(target => target.Rule), AutoRunJson.Options),
            JsonSerializer.Serialize(replay.Targets.Select(target => target.Rule), AutoRunJson.Options));
        Assert.Equal(original.Targets.Select(target => target.Selected), replay.Targets.Select(target => target.Selected));
        var replayItems = fixture.Service.Items(replay.Id, 0, 10, null).Items;
        Assert.Equal(JsonSerializer.Serialize(originalItems.Select(item => new { item.OverlayId, item.Targets })),
            JsonSerializer.Serialize(replayItems.Select(item => new { item.OverlayId, item.Targets })));
        Assert.All(replayItems, item => Assert.Equal(AutoRunItemOutcome.Pending, item.Outcome));
        Assert.Equal(originalJson, JsonSerializer.Serialize(fixture.Service.Get(original.Id) with { Memory = null, ServerTime = default }, AutoRunJson.Options));
        Assert.Equal(calls.Length, fixture.Publisher.Calls.Count);

        fixture.Service.Delete(original.Id);
        fixture.Service.Start(replay.Id);
        await fixture.WaitAsync(replay.Id, AutoRunState.Completed);
        var replayCalls = fixture.Publisher.Calls.Where(call => call.RunId == replay.Id).ToArray();
        Assert.Equal(calls.Length, replayCalls.Length);
        foreach (var call in calls)
            Assert.Equal(call.Payload, Assert.Single(replayCalls, replayCall => replayCall.OverlayId == call.OverlayId).Payload);
        Assert.Equal(providerCalls, (fixture.Provider.GetCalls.Count, fixture.Provider.SearchCursors.Count, fixture.Provider.SensorTypeCalls.Count));
        Assert.Equal(ruleCalls, fixture.Resolver.Calls);
    }

    [Fact]
    public async Task RepublishResetsEverySelectedItemIncludingConfirmedFailedAndUncertainOutcomes()
    {
        var replaying = false;
        var publisher = new RecordingPublisher((call, _) => Task.FromResult(new AutoRunPublishResult(replaying
            ? AutoRunItemOutcome.Confirmed : call.OverlayId switch
            {
                "confirmed" => AutoRunItemOutcome.Confirmed,
                "failed" => AutoRunItemOutcome.Failed,
                _ => AutoRunItemOutcome.Uncertain
            }, replaying ? null : "Original publication outcome")));
        await using var fixture = await Fixture.StartAsync([Overlay("confirmed"), Overlay("failed"), Overlay("uncertain")], publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(["confirmed", "failed", "uncertain"], []));
        fixture.Service.Start(ready.Id);
        var original = await fixture.WaitAsync(ready.Id, AutoRunState.CompletedWithErrors);
        Assert.True(original.CanRepublish);
        Assert.Equal((1, 1, 1), (original.Counters.Confirmed, original.Counters.Failed, original.Counters.Uncertain));

        replaying = true;
        var replay = fixture.Service.Republish(original.Id, CancellationToken.None);
        Assert.Equal(3, replay.Counters.Remaining);
        Assert.All(fixture.Service.Items(replay.Id, 0, 10, null).Items, item =>
        {
            Assert.Equal(AutoRunItemOutcome.Pending, item.Outcome);
            Assert.Null(item.Error);
        });
        fixture.Service.Start(replay.Id);
        var completed = await fixture.WaitAsync(replay.Id, AutoRunState.Completed);
        Assert.Equal(3, completed.Counters.Confirmed);
        Assert.Equal(6, publisher.Calls.Count);
        Assert.Equal(original.Counters, fixture.Service.Get(original.Id).Counters);
        Assert.Equal(3, fixture.Provider.GetCalls.Count);
    }

    [Theory]
    [InlineData("jobs")]
    [InlineData("bytes")]
    [InlineData("memory")]
    [InlineData("cancellation")]
    public async Task RepublishRejectedAdmissionDoesNotLeakRunsOrRetainedBytes(string limit)
    {
        var options = EnabledOptions();
        var memory = new AutoRunMemoryTestGuard();
        await using var fixture = await Fixture.StartAsync([Overlay("one")], options, memory: memory);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        var retained = fixture.Service.Execution().RetainedBytes;
        if (limit == "jobs") options.MaxRetainedJobs = 1;
        if (limit == "bytes") options.PayloadBudgetBytes = retained;
        if (limit == "memory") memory.Blocked = true;
        using var cancelled = new CancellationTokenSource();
        if (limit == "cancellation") cancelled.Cancel();

        if (limit == "cancellation")
            Assert.ThrowsAny<OperationCanceledException>(() => fixture.Service.Republish(ready.Id, cancelled.Token));
        else
            Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Republish(ready.Id, cancelled.Token)).StatusCode);

        Assert.Equal(retained, fixture.Service.Execution().RetainedBytes);
        Assert.Equal(ready.Id, Assert.Single(fixture.Service.List()).Id);
        Assert.Single(fixture.Publisher.Calls);
    }

    [Theory]
    [InlineData("ready", 409)]
    [InlineData("cancelled", 409)]
    [InlineData("expired", 409)]
    [InlineData("failed", 409)]
    [InlineData("missing", 404)]
    public async Task RepublishRejectsIneligibleRunsAndOtherTerminalStatesReleasePayloads(string state, int status)
    {
        var clock = new ManualTimeProvider();
        var overlay = Overlay("one");
        await using var fixture = await Fixture.StartAsync([overlay], time: clock);
        if (state == "failed") fixture.Provider.Get = (_, _) => throw new ElasticProviderException("Provider failure.");
        var accepted = await fixture.Service.PreviewAsync(Request(["one"], []), CancellationToken.None);
        var ready = await fixture.WaitAsync(accepted.Id, state == "failed" ? AutoRunState.Failed : AutoRunState.Ready);
        if (state == "cancelled") fixture.Service.Cancel(ready.Id);
        if (state == "expired") clock.Advance(TimeSpan.FromMinutes(31));
        var current = fixture.Service.Get(ready.Id);
        Assert.False(current.CanRepublish);
        if (state is "cancelled" or "expired") Assert.Equal(overlay.Payload.Length, ready.RetainedBytes - current.RetainedBytes);

        Assert.Equal(status, Assert.Throws<AutoRunRequestException>(() =>
            fixture.Service.Republish(state == "missing" ? "missing" : ready.Id, CancellationToken.None)).StatusCode);
        Assert.Single(fixture.Service.List());
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task RepublishUsesNormalPublicationQueueAndStartRemainsIdempotent()
    {
        var publishingReplays = false;
        var response = new TaskCompletionSource<AutoRunPublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher((_, _) =>
        {
            if (!publishingReplays) return Task.FromResult(new AutoRunPublishResult(AutoRunItemOutcome.Confirmed));
            requested.TrySetResult();
            return response.Task;
        });
        var options = EnabledOptions();
        options.MaxConcurrentPublications = 1;
        options.MaxQueuedJobs = 1;
        await using var fixture = await Fixture.StartAsync([Overlay("one")], options, publisher);
        var original = await fixture.ReadyAsync(Request(["one"], []));
        fixture.Service.Start(original.Id);
        await fixture.WaitAsync(original.Id, AutoRunState.Completed);
        var first = fixture.Service.Republish(original.Id, CancellationToken.None);
        var second = fixture.Service.Republish(original.Id, CancellationToken.None);
        var third = fixture.Service.Republish(original.Id, CancellationToken.None);
        try
        {
            publishingReplays = true;
            fixture.Service.Start(first.Id);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(AutoRunState.Publishing, fixture.Service.Start(first.Id).State);
            Assert.Equal(2, publisher.Calls.Count);
            Assert.Equal(AutoRunState.QueuedPublication, fixture.Service.Start(second.Id).State);
            Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(third.Id)).StatusCode);
            Assert.Equal(AutoRunState.Ready, fixture.Service.Get(third.Id).State);
            foreach (var id in new[] { first.Id, second.Id })
                Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Republish(id, CancellationToken.None)).StatusCode);

            Assert.Equal([first.Id], fixture.Service.Delete(first.Id).PendingIds);
            Assert.True(fixture.Service.Get(first.Id).DeletionRequested);
            Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Republish(first.Id, CancellationToken.None)).StatusCode);
            response.SetResult(new(AutoRunItemOutcome.Confirmed));
            await fixture.WaitAsync(second.Id, AutoRunState.Completed);
            await UntilAsync(() => fixture.Service.List().All(job => job.Id != first.Id));
            Assert.Equal(3, publisher.Calls.Count);
            Assert.Single(fixture.Provider.GetCalls);
        }
        finally { response.TrySetResult(new(AutoRunItemOutcome.Confirmed)); }
    }

    [Fact]
    public async Task CompletedPayloadsExpireWhileAnIndependentPublicationPreviewRemainsUsable()
    {
        var clock = new ManualTimeProvider();
        var options = EnabledOptions();
        options.SummaryLifetimeMinutes = 1;
        await using var fixture = await Fixture.StartAsync([Overlay("one")], options, time: clock);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        await UntilAsync(() => fixture.Service.Execution().ActivePublications == 0);
        var replay = fixture.Service.Republish(ready.Id, CancellationToken.None);
        clock.Advance(TimeSpan.FromMinutes(2));

        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Republish(ready.Id, CancellationToken.None)).StatusCode);
        Assert.Equal(replay.RetainedBytes, fixture.Service.Execution().RetainedBytes);
        fixture.Service.Start(replay.Id);
        await fixture.WaitAsync(replay.Id, AutoRunState.Completed);
        Assert.Equal(2, fixture.Publisher.Calls.Count);
        Assert.Single(fixture.Provider.GetCalls);
    }
}
