using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Historical;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunServiceTests
{
    [Fact]
    public void SelectorSharesOverlaysAcrossQuotasButCountsThemOnceGlobally()
    {
        var candidates = new[]
        {
            Candidate("both", [0, 1]), Candidate("a-only", [0]), Candidate("b-only", [1])
        };
        var selected = AutoRunSelector.Select(candidates, [Target("a", 2, 2), Target("b", 2, 2)], 3);

        Assert.Equal(3, selected.Count);
        Assert.Equal(2, selected.Count(item => item.Assignments.Contains(0)));
        Assert.Equal(2, selected.Count(item => item.Assignments.Contains(1)));
        Assert.Equal([0, 1], selected.Single(item => item.Candidate.Overlay.Input.ImageId == "both").Assignments);
    }

    [Fact]
    public void SelectorNeverExceedsEitherPerRuleMaximumOrOverallMaximum()
    {
        var candidates = Enumerable.Range(0, 10).Select(i => Candidate($"overlay-{i}", [0, 1])).ToArray();

        var selected = AutoRunSelector.Select(candidates, [Target("a", 0, 1), Target("b", 0, 3)], 2);

        Assert.Equal(2, selected.Count);
        Assert.Single(selected, item => item.Assignments.Contains(0));
        Assert.Equal(2, selected.Count(item => item.Assignments.Contains(1)));
        Assert.Single(selected[1].Assignments);
    }

    [Fact]
    public void SelectorPrioritizesUnmetMinimumsThenUsesNewestAndStableIdOrder()
    {
        var old = Overlay("old-required", photoTime: "2020-01-01T00:00:00Z");
        var recent = Overlay("newest-optional", photoTime: "2024-01-01T00:00:00Z");
        var selected = AutoRunSelector.Select(
            [new(recent, [0]), new(old, [1])], [Target("a", 0, 2), Target("b", 1, 2)], 1);
        Assert.Equal("old-required", Assert.Single(selected).Candidate.Overlay.Input.ImageId);

        var byDate = AutoRunSelector.Select(
            [Candidate("z", [0]), new(recent, [0]), Candidate("a", [0])], [Target("a", 0, 3)], 3);
        Assert.Equal(["newest-optional", "a", "z"], byDate.Select(item => item.Candidate.Overlay.Input.ImageId));
    }

    [Fact]
    public async Task PreviewReportsShortfallsAndPublishesAvailableOverlappingAssignments()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("only")]);
        var request = Request(["only"], [Target("a", 2, 2), Target("b", 2, 2)]) with
        {
            MinOverlays = 2, MaxOverlays = 2
        };
        var ready = await fixture.ReadyAsync(request);

        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(1, ready.Shortfall);
        Assert.All(ready.Targets, target => { Assert.Equal(1, target.Selected); Assert.Equal(1, target.Shortfall); });
        Assert.NotEmpty(ready.Warnings);
        fixture.Service.Start(ready.Id);
        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);

        Assert.Equal(1, complete.Counters.Confirmed);
        Assert.All(complete.Targets, target => Assert.Equal(1, target.Confirmed));
        Assert.Equal(2, Assert.Single(fixture.Publisher.Calls).Targets.Count);
    }

    [Fact]
    public async Task PreviewFreezesIdsRulesAndPayloadsUntilPublication()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("frozen")]);
        var request = Request(["frozen"]);
        var ready = await fixture.ReadyAsync(request);
        var rule = fixture.Resolver.Rules["rule-a"];
        rule.IsActive = false;
        rule.RunParams = [RunParams("changed-after-preview")];
        request.Source.OverlayIds![0] = "different";
        request.Targets.Clear();
        fixture.Provider.Overlays["frozen"] = Overlay("frozen", imageUrl: "https://changed.invalid/image");

        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);

        var call = Assert.Single(fixture.Publisher.Calls);
        using var payload = JsonDocument.Parse(call.Payload);
        Assert.Equal("frozen", payload.RootElement.GetProperty("id").GetString());
        Assert.Equal("https://example.invalid/image", payload.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("original-tenant", Assert.Single(call.Targets).Rule.RunParams![0].GetProperty("tenantId").GetString());
        Assert.True(call.Targets[0].Rule.IsActive);
        Assert.Equal(1, fixture.Resolver.Calls);
        Assert.Equal(["frozen"], fixture.Provider.GetCalls);
    }

    [Theory]
    [InlineData("scanLimit")]
    [InlineData("candidateLimit")]
    public async Task PreparationBoundsScanningAndCandidateRetentionAndReportsPartialSource(string limit)
    {
        var options = EnabledOptions();
        if (limit == "scanLimit") options.MaxScannedRecords = 2;
        else options.MaxCandidates = 2;
        await using var fixture = await Fixture.StartAsync(
            [Overlay("one"), Overlay("two"), Overlay("three")], options);
        var ready = await fixture.ReadyAsync(Request(["one", "two", "three"]));

        Assert.Equal(2, ready.Counters.Scanned);
        Assert.Equal(2, ready.Counters.Selected);
        Assert.Equal(limit, ready.StoppedReason);
        Assert.False(ready.SourceExhausted);
        Assert.NotEmpty(ready.Warnings);
        Assert.Equal(2, fixture.Provider.GetCalls.Count);
    }

    [Fact]
    public async Task SearchFollowsPagesDeduplicatesIdsAndCountsInvalidRecords()
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Provider.Search = (_, next, _) => Task.FromResult(next is null
            ? new ProviderPage([new(Overlay("same"), null), new(null, "broken record")], "/next")
            : new ProviderPage([new(Overlay("same"), null), new(Overlay("other"), null)], null));
        var ready = await fixture.ReadyAsync(Request([]) with { Source = new() { Mode = AutoRunSourceMode.Filters } });

        Assert.Equal([null, "/next"], fixture.Provider.SearchCursors);
        Assert.Equal(4, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Invalid);
        Assert.Equal(1, ready.Counters.Duplicates);
        Assert.Equal(2, ready.Counters.Selected);
        Assert.True(ready.SourceExhausted);
    }

    [Fact]
    public async Task MultipleRuleSearchesAlternatePagesAndKeepEachBranchCursor()
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Resolver.Rules["rule-a"].Match = new() { Resolution = new() { Min = 0.5 } };
        fixture.Resolver.Rules["rule-b"].Match = new() { Resolution = new() { Min = 0.6 } };
        var branches = new Dictionary<string, int>(StringComparer.Ordinal);
        var requests = new List<(int Branch, string? Cursor)>();
        fixture.Provider.Search = (query, next, _) =>
        {
            var key = query.GetRawText();
            if (!branches.TryGetValue(key, out var branch)) branches.Add(key, branch = branches.Count);
            requests.Add((branch, next));
            return Task.FromResult(next is null
                ? new ProviderPage([new(Overlay($"branch-{branch}-first"), null)], $"/branch-{branch}-next")
                : new ProviderPage([new(Overlay($"branch-{branch}-last"), null)], null));
        };

        var ready = await fixture.ReadyAsync(Request([], [Target("a"), Target("b")]) with
        {
            Source = new() { Mode = AutoRunSourceMode.Rules }
        });

        Assert.Equal(2, branches.Count);
        Assert.Equal([0, 1, 0, 1], requests.Select(request => request.Branch));
        Assert.Equal([null, null, "/branch-0-next", "/branch-1-next"], requests.Select(request => request.Cursor));
        Assert.Equal(4, ready.Counters.Scanned);
        Assert.Equal(4, ready.Counters.Selected);
        Assert.True(ready.SourceExhausted);
    }

    [Fact]
    public async Task RepeatingPageCursorFailsPreviewRatherThanLooping()
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Provider.Search = (_, _, _) => Task.FromResult(
            new ProviderPage([new(Overlay("same"), null)], "/repeated"));
        var admitted = await fixture.Service.PreviewAsync(
            Request([]) with { Source = new() { Mode = AutoRunSourceMode.Filters } }, CancellationToken.None);

        var failed = await fixture.WaitAsync(admitted.Id, AutoRunState.Failed);

        Assert.Equal(2, fixture.Provider.SearchCursors.Count);
        Assert.Contains(failed.Errors, error => error.Contains("repeated", StringComparison.OrdinalIgnoreCase));
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task ByteBudgetFailureReleasesCandidatesAndAllowsLaterPreview()
    {
        var oversized = Overlay("oversized", imageUrl: "https://example.invalid/" + new string('a', 8000));
        var options = EnabledOptions();
        options.PayloadBudgetBytes = 5000;
        await using var fixture = await Fixture.StartAsync([Overlay("small"), oversized], options);
        var first = await fixture.Service.PreviewAsync(Request(["small", "oversized"]), CancellationToken.None);
        var failed = await fixture.WaitAsync(first.Id, AutoRunState.Failed);

        Assert.Equal(0, failed.Counters.Selected);
        Assert.Contains(failed.Errors, error => error.Contains("payload budget", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(first.Id)).StatusCode);
        var next = await fixture.ReadyAsync(Request(["small"]));
        Assert.Equal(1, next.Counters.Selected);
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task ReadyPreviewsShareTheProcessPayloadBudget()
    {
        var options = EnabledOptions();
        options.PayloadBudgetBytes = 5000;
        await using var fixture = await Fixture.StartAsync(
            [Overlay("large", imageUrl: "https://example.invalid/" + new string('a', 2200))], options);
        var first = await fixture.ReadyAsync(Request(["large"]));
        var second = await fixture.Service.PreviewAsync(Request(["large"]), CancellationToken.None);
        await fixture.WaitAsync(second.Id, AutoRunState.Failed);
        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Cancel(first.Id).State);

        var afterRelease = await fixture.ReadyAsync(Request(["large"]));
        Assert.Equal(1, afterRelease.Counters.Selected);
    }

    [Fact]
    public async Task PreviewExpiryPreventsStartAndSummaryRetentionEventuallyRemovesIt()
    {
        var options = EnabledOptions();
        options.ReadyLifetimeMinutes = 1;
        options.SummaryLifetimeMinutes = 2;
        var time = new ManualTimeProvider();
        await using var fixture = await Fixture.StartAsync([Overlay("one")], options, time: time);
        var ready = await fixture.ReadyAsync(Request(["one"]));
        time.Advance(TimeSpan.FromMinutes(1));

        Assert.Equal(AutoRunState.Expired, fixture.Service.Get(ready.Id).State);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(ready.Id)).StatusCode);
        time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Get(ready.Id)).StatusCode);
        Assert.Empty(fixture.Service.List());
    }

    [Fact]
    public async Task RestartHasNoPreviewDurability()
    {
        string id;
        await using (var first = await Fixture.StartAsync([Overlay("one")]))
            id = (await first.ReadyAsync(Request(["one"]))).Id;
        await using var restarted = await Fixture.StartAsync([Overlay("one")]);

        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => restarted.Service.Get(id)).StatusCode);
        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => restarted.Service.Start(id)).StatusCode);
    }

    [Fact]
    public async Task RepeatedStartIsIdempotentButNewPreviewDeliberatelyPublishesAgain()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var first = await fixture.ReadyAsync(Request(["one"]));
        fixture.Service.Start(first.Id);
        fixture.Service.Start(first.Id);
        await fixture.WaitAsync(first.Id, AutoRunState.Completed);
        Assert.Equal(AutoRunState.Completed, fixture.Service.Start(first.Id).State);
        Assert.Single(fixture.Publisher.Calls);

        var second = await fixture.ReadyAsync(Request(["one"]));
        fixture.Service.Start(second.Id);
        await fixture.WaitAsync(second.Id, AutoRunState.Completed);
        Assert.Equal(2, fixture.Publisher.Calls.Count);
        Assert.Equal(2, fixture.Publisher.Calls.Select(call => call.RunId).Distinct().Count());
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(100, 4)]
    public async Task BatchLimitsBoundConcurrentPublication(int batchSize, int expectedConcurrency)
    {
        var releases = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(async (_, token) =>
        {
            await releases.Task.WaitAsync(token);
            return new(AutoRunItemOutcome.Confirmed);
        });
        var overlays = Enumerable.Range(0, 100).Select(i => Overlay($"overlay-{i:D3}")).ToArray();
        await using var fixture = await Fixture.StartAsync(overlays, publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(overlays.Select(o => o.Input.ImageId).ToArray()) with
        {
            Publication = new() { BatchSize = batchSize, DelayMs = 0 }
        });
        fixture.Service.Start(ready.Id);
        await UntilAsync(() => publisher.Active == expectedConcurrency);

        Assert.Equal(expectedConcurrency, publisher.Calls.Count);
        Assert.Equal(expectedConcurrency, fixture.Service.Get(ready.Id).Counters.InFlight);
        releases.SetResult();
        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);

        Assert.Equal(100, complete.Counters.Confirmed);
        Assert.Equal(expectedConcurrency, publisher.MaxActive);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    public async Task InvalidBatchSizesAreRejectedAtAdmissionAndUpdate(int batchSize)
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var invalid = new AutoRunPublication { BatchSize = batchSize, DelayMs = 0 };
        var error = await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(Request(["one"]) with { Publication = invalid }, CancellationToken.None));
        Assert.Equal(400, error.StatusCode);
        Assert.Equal(0, fixture.Resolver.Calls);
        var ready = await fixture.ReadyAsync(Request(["one"]));
        Assert.Equal(400, Assert.Throws<AutoRunRequestException>(() =>
            fixture.Service.UpdatePublication(ready.Id, invalid)).StatusCode);
    }

    [Fact]
    public async Task FailedAndUncertainPublicationsDoNotStopOtherItemsOrAutomaticallyRetry()
    {
        var publisher = new RecordingPublisher((call, _) => call.OverlayId switch
        {
            "failed" => Task.FromResult(new AutoRunPublishResult(AutoRunItemOutcome.Failed, "rejected")),
            "uncertain" => throw new IOException("confirmation lost"),
            _ => Task.FromResult(new AutoRunPublishResult(AutoRunItemOutcome.Confirmed))
        });
        await using var fixture = await Fixture.StartAsync(
            [Overlay("confirmed"), Overlay("failed"), Overlay("uncertain")], publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(["confirmed", "failed", "uncertain"]));
        fixture.Service.Start(ready.Id);

        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.CompletedWithErrors);

        Assert.Equal(1, complete.Counters.Confirmed);
        Assert.Equal(1, complete.Counters.Failed);
        Assert.Equal(1, complete.Counters.Uncertain);
        Assert.Equal(0, complete.Counters.Remaining);
        Assert.Equal(3, publisher.Calls.Count);
        Assert.Equal("uncertain", Assert.Single(fixture.Service.Items(ready.Id, 0, 10, AutoRunItemOutcome.Uncertain).Items).OverlayId);
        Assert.All(complete.Targets, target =>
        {
            Assert.Equal(1, target.Confirmed);
            Assert.Equal(1, target.Failed);
            Assert.Equal(1, target.Uncertain);
        });
    }

    [Fact]
    public async Task PublicationTimeoutMarksSlowItemUncertainOnceAndContinuesLaterBatches()
    {
        var options = EnabledOptions();
        options.PublishTimeoutSeconds = 1;
        var slowStarted = new TaskCompletionSource<long>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publisher = new RecordingPublisher(async (call, token) =>
        {
            if (call.OverlayId == "a-slow")
            {
                slowStarted.SetResult(Stopwatch.GetTimestamp());
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return new(AutoRunItemOutcome.Confirmed);
        });
        await using var fixture = await Fixture.StartAsync(
            [Overlay("a-slow"), Overlay("b-later")], options, publisher);
        var ready = await fixture.ReadyAsync(Request(["a-slow", "b-later"]) with
        {
            Publication = new() { BatchSize = 1, DelayMs = 0 }
        });
        fixture.Service.Start(ready.Id);
        var startedAt = await slowStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.CompletedWithErrors);

        // A lower bound verifies the configured timeout without imposing a scheduler-dependent upper bound.
        Assert.True(Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromMilliseconds(900));
        Assert.Equal(1, complete.Counters.Uncertain);
        Assert.Equal(1, complete.Counters.Confirmed);
        Assert.Equal(0, complete.Counters.Failed);
        Assert.Equal(0, complete.Counters.Remaining);
        Assert.Equal(["a-slow", "b-later"], publisher.Calls.Select(call => call.OverlayId));
        Assert.Equal("a-slow", Assert.Single(fixture.Service.Items(ready.Id, 0, 10, AutoRunItemOutcome.Uncertain).Items).OverlayId);
        Assert.Equal("b-later", Assert.Single(fixture.Service.Items(ready.Id, 0, 10, AutoRunItemOutcome.Confirmed).Items).OverlayId);
    }

    [Fact]
    public async Task NonzeroInterBatchDelayPreventsNextPublicationUntilTimerFires()
    {
        var time = new ControlledDelayTimeProvider();
        await using var fixture = await Fixture.StartAsync([Overlay("first"), Overlay("second")], time: time);
        var ready = await fixture.ReadyAsync(Request(["first", "second"]) with
        {
            Publication = new() { BatchSize = 1, DelayMs = 750 }
        });
        fixture.Service.Start(ready.Id);
        var delay = await time.NextDelayAsync();

        Assert.Equal(TimeSpan.FromMilliseconds(750), delay.DueTime);
        Assert.Single(fixture.Publisher.Calls);
        Assert.Equal(1, fixture.Service.Get(ready.Id).Counters.Confirmed);
        Assert.Equal(AutoRunState.Publishing, fixture.Service.Get(ready.Id).State);
        delay.Fire();
        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);

        Assert.Equal(2, complete.Counters.Confirmed);
        Assert.Equal(["first", "second"], fixture.Publisher.Calls.Select(call => call.OverlayId));
    }

    [Fact]
    public async Task CancellationInterruptsInterBatchDelayWithoutSubmittingNextItem()
    {
        var time = new ControlledDelayTimeProvider();
        await using var fixture = await Fixture.StartAsync([Overlay("first"), Overlay("second")], time: time);
        var ready = await fixture.ReadyAsync(Request(["first", "second"]) with
        {
            Publication = new() { BatchSize = 1, DelayMs = 60_000 }
        });
        fixture.Service.Start(ready.Id);
        var delay = await time.NextDelayAsync();
        Assert.Single(fixture.Publisher.Calls);

        fixture.Service.Cancel(ready.Id);
        var cancelled = await fixture.WaitAsync(ready.Id, AutoRunState.Cancelled);

        Assert.True(delay.IsDisposed);
        Assert.Equal(1, cancelled.Counters.Confirmed);
        Assert.Equal(0, cancelled.Counters.Uncertain);
        Assert.Equal(1, cancelled.Counters.Remaining);
        Assert.Single(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task PublicationUpdatesApplyAtNextBatchBoundary()
    {
        var blocked = Channel.CreateUnbounded<BlockedPublication>();
        var publisher = new RecordingPublisher(async (call, token) =>
        {
            var completion = new TaskCompletionSource<AutoRunPublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            await blocked.Writer.WriteAsync(new(call, completion), token);
            return await completion.Task.WaitAsync(token);
        });
        await using var fixture = await Fixture.StartAsync(
            [Overlay("one"), Overlay("two"), Overlay("three"), Overlay("four")], publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(["one", "two", "three", "four"]) with
        {
            Publication = new() { BatchSize = 2, DelayMs = 0 }
        });
        fixture.Service.Start(ready.Id);
        var first = await blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var second = await blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));

        var pending = fixture.Service.UpdatePublication(ready.Id, new() { BatchSize = 1, DelayMs = 0 });
        Assert.True(pending.PublicationChangePending);
        Assert.Equal(2, pending.ActivePublication.BatchSize);
        Assert.Equal(1, pending.Publication.BatchSize);
        first.Completion.SetResult(new(AutoRunItemOutcome.Confirmed));
        second.Completion.SetResult(new(AutoRunItemOutcome.Confirmed));
        var third = await blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        var active = fixture.Service.Get(ready.Id);
        Assert.False(active.PublicationChangePending);
        Assert.Equal(1, active.ActivePublication.BatchSize);
        Assert.Equal(1, active.Counters.InFlight);
        Assert.Equal(3, publisher.Calls.Count);
        third.Completion.SetResult(new(AutoRunItemOutcome.Confirmed));
        var fourth = await blocked.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        fourth.Completion.SetResult(new(AutoRunItemOutcome.Confirmed));
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
    }

    [Fact]
    public async Task CancelStopsSchedulingAndRetainsUncertainInFlightOutcomes()
    {
        var publisher = new RecordingPublisher(async (_, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(AutoRunItemOutcome.Confirmed);
        });
        var overlays = Enumerable.Range(0, 10).Select(i => Overlay($"overlay-{i}")).ToArray();
        await using var fixture = await Fixture.StartAsync(overlays, publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(overlays.Select(o => o.Input.ImageId).ToArray()) with
        {
            Publication = new() { BatchSize = 100, DelayMs = 0 }
        });
        fixture.Service.Start(ready.Id);
        await UntilAsync(() => publisher.Active == 4);

        fixture.Service.Cancel(ready.Id);
        var cancelled = await fixture.WaitAsync(ready.Id, AutoRunState.Cancelled);

        Assert.Equal(4, publisher.Calls.Count);
        Assert.Equal(4, cancelled.Counters.Uncertain);
        Assert.Equal(6, cancelled.Counters.Remaining);
        Assert.Equal(0, cancelled.Counters.InFlight);
        Assert.Equal(0, cancelled.Counters.Confirmed);
        Assert.NotEmpty(cancelled.Warnings);
    }

    [Fact]
    public async Task CancelPreparingReleasesAdmissionAndReadyCancellationDoesNotPublish()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Get = async (_, token) =>
        {
            requested.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(Overlay("one"), null);
        };
        var first = await fixture.Service.PreviewAsync(Request(["one"]), CancellationToken.None);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(Request(["one"]), CancellationToken.None));
        Assert.Equal(409, concurrent.StatusCode);
        fixture.Service.Cancel(first.Id);
        await fixture.WaitAsync(first.Id, AutoRunState.Cancelled);
        fixture.Provider.Get = null;

        var second = await fixture.ReadyAsync(Request(["one"]));
        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Cancel(second.Id).State);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(second.Id)).StatusCode);
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task InvalidAndDuplicateTargetsAreRejectedBeforeResolvingRules()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var invalidRequests = new[]
        {
            Request(["one"], [Target("a", 2, 1)]),
            Request(["one"], [Target("a", 0, 1), Target("a", 0, 1)]),
            Request(["one"]) with { MinOverlays = 2, MaxOverlays = 1 },
            Request(["one"]) with { Source = new() { Mode = (AutoRunSourceMode)99 } }
        };
        foreach (var request in invalidRequests)
            Assert.Equal(400, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
                fixture.Service.PreviewAsync(request, CancellationToken.None))).StatusCode);
        Assert.Equal(0, fixture.Resolver.Calls);
    }

    private static AutoRunOptions EnabledOptions() => new()
    {
        Enabled = true, PublisherConcurrency = 4, Publication = new() { BatchSize = 10, DelayMs = 0 }
    };

    private static AutoRunTarget Target(string name, int minimum = 0, int maximum = 100) =>
        new() { PipelineId = $"pipeline-{name}", RuleId = $"rule-{name}", MinOverlays = minimum, MaxOverlays = maximum };

    private static AutoRunPreviewRequest Request(string[] ids, List<AutoRunTarget>? targets = null) => new()
    {
        Source = new() { Mode = AutoRunSourceMode.OverlayIds, OverlayIds = ids },
        Targets = targets ?? [Target("a")],
        Publication = new() { BatchSize = 10, DelayMs = 0 }
    };

    private static JsonElement RunParams(string tenant) => JsonSerializer.SerializeToElement(new
    {
        tenantId = tenant, algorithmNames = new[] { "FindAir" },
        tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
    });

    private static PipelineRuleDocument Rule(string id) => new()
    {
        Id = id, RuleName = id, IsActive = true,
        Match = new() { MaxPhotoAgeDays = 1 },
        RunParams = [RunParams("original-tenant")]
    };

    private static AutoRunCandidate Candidate(string id, int[] matches) => new(Overlay(id), matches);

    private static ProviderOverlay Overlay(
        string id, string photoTime = "2020-01-02T10:00:00Z", string imageUrl = "https://example.invalid/image")
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, sensorName = "camera", sensorType = "EO", registrationQuality = "Accurate",
            bestResolution = 0.7, imageUrl, width = 100, height = 100, photoTime,
            gridType = "grid-a", gridURI = "https://example.invalid/grid",
            roiFootprint = new { type = "Polygon", coordinates = new[] { new[] { new[] { 0, 0 }, new[] { 1, 0 }, new[] { 1, 1 }, new[] { 0, 1 }, new[] { 0, 0 } } } }
        });
        return new(payload, GatewayInputMessageParser.Parse(payload), DateTimeOffset.Parse(photoTime), false, "sortie");
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        var started = Stopwatch.StartNew();
        while (!condition())
        {
            if (started.Elapsed > TimeSpan.FromSeconds(5)) throw new TimeoutException("Auto-run did not reach the expected state.");
            await Task.Delay(10);
        }
    }

    private sealed record BlockedPublication(PublicationCall Call, TaskCompletionSource<AutoRunPublishResult> Completion);
    private sealed record PublicationCall(string RunId, string OverlayId, byte[] Payload, IReadOnlyList<AutoRunResolvedTarget> Targets);

    private sealed class RecordingPublisher(
        Func<PublicationCall, CancellationToken, Task<AutoRunPublishResult>>? action = null) : IAutoRunPublisher
    {
        private int _active;
        private int _maxActive;
        public int Active => Volatile.Read(ref _active);
        public int MaxActive => Volatile.Read(ref _maxActive);
        public ConcurrentQueue<PublicationCall> Calls { get; } = new();

        public async Task<AutoRunPublishResult> PublishAsync(
            string runId, byte[] payload, IReadOnlyList<AutoRunResolvedTarget> targets, CancellationToken cancellationToken)
        {
            using var json = JsonDocument.Parse(payload);
            var call = new PublicationCall(runId, json.RootElement.GetProperty("id").GetString()!, payload.ToArray(), targets.ToArray());
            Calls.Enqueue(call);
            var active = Interlocked.Increment(ref _active);
            int observed;
            do { observed = Volatile.Read(ref _maxActive); }
            while (active > observed && Interlocked.CompareExchange(ref _maxActive, active, observed) != observed);
            try
            {
                return action is null ? new(AutoRunItemOutcome.Confirmed) : await action(call, cancellationToken);
            }
            finally { Interlocked.Decrement(ref _active); }
        }
    }

    private sealed class FakeProvider(IEnumerable<ProviderOverlay> overlays) : IElasticOverlayProvider
    {
        public Dictionary<string, ProviderOverlay> Overlays { get; } = overlays.ToDictionary(o => o.Input.ImageId, StringComparer.Ordinal);
        public ConcurrentQueue<string> GetCalls { get; } = new();
        public ConcurrentQueue<string?> SearchCursors { get; } = new();
        public Func<string, CancellationToken, Task<ProviderOverlayResult>>? Get { get; set; }
        public Func<JsonElement, string?, CancellationToken, Task<ProviderPage>>? Search { get; set; }

        public Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken)
        {
            GetCalls.Enqueue(id);
            return Get is { } handler ? handler(id, cancellationToken)
                : Task.FromResult(Overlays.TryGetValue(id, out var overlay)
                    ? new ProviderOverlayResult(overlay, null) : new(null, "Missing overlay."));
        }

        public Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken)
        {
            SearchCursors.Enqueue(nextPage);
            return Search is { } handler ? handler(query, nextPage, cancellationToken)
                : Task.FromResult(new ProviderPage(Overlays.Values.Select(o => new ProviderOverlayResult(o, null)).ToArray(), null));
        }
    }

    private sealed class FakeResolver : IAutoRunRuleResolver
    {
        public Dictionary<string, PipelineRuleDocument> Rules { get; } = new(StringComparer.Ordinal)
        {
            ["rule-a"] = Rule("rule-a"), ["rule-b"] = Rule("rule-b")
        };
        public int Calls { get; private set; }
        public Task<IReadOnlyList<AutoRunResolvedTarget>> ResolveAsync(
            IReadOnlyList<AutoRunTarget> targets, CancellationToken cancellationToken)
        {
            Calls++;
            var contract = new FindAirPipelineContract();
            IReadOnlyList<AutoRunResolvedTarget> resolved = targets.Select(target =>
            {
                var rule = JsonSerializer.Deserialize<PipelineRuleDocument>(
                    JsonSerializer.SerializeToUtf8Bytes(Rules[target.RuleId], AutoRunJson.Options), AutoRunJson.Options)!;
                return new AutoRunResolvedTarget(target with { }, rule, HistoricalRuleSnapshot.Build(rule, contract), contract);
            }).ToArray();
            return Task.FromResult(resolved);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }

    /// <summary>Holds one-shot delay timers; periodic housekeeping continues to use the system clock.</summary>
    private sealed class ControlledDelayTimeProvider : TimeProvider
    {
        private readonly Channel<ControlledDelayTimer> _delays = Channel.CreateUnbounded<ControlledDelayTimer>();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (period != Timeout.InfiniteTimeSpan)
                return base.CreateTimer(callback, state, dueTime, period);
            var timer = new ControlledDelayTimer(callback, state, dueTime);
            _delays.Writer.TryWrite(timer);
            return timer;
        }

        public Task<ControlledDelayTimer> NextDelayAsync() =>
            _delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
    }

    private sealed class ControlledDelayTimer(TimerCallback callback, object? state, TimeSpan dueTime) : ITimer
    {
        private int _disposed;
        private int _fired;
        public TimeSpan DueTime { get; private set; } = dueTime;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public bool Change(TimeSpan newDueTime, TimeSpan period)
        {
            if (IsDisposed) return false;
            DueTime = newDueTime;
            return true;
        }

        public void Fire()
        {
            if (!IsDisposed && Interlocked.Exchange(ref _fired, 1) == 0) callback(state);
        }

        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(ProviderOverlay[] overlays, AutoRunOptions options, RecordingPublisher? publisher, TimeProvider? time)
        {
            Resolver = new();
            Provider = new(overlays);
            Publisher = publisher ?? new();
            Service = new(Resolver, Provider, new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions())),
                Publisher, Options.Create(options), time ?? TimeProvider.System, NullLogger<AutoRunService>.Instance);
        }

        public FakeResolver Resolver { get; }
        public FakeProvider Provider { get; }
        public RecordingPublisher Publisher { get; }
        public AutoRunService Service { get; }

        public static async Task<Fixture> StartAsync(
            ProviderOverlay[] overlays, AutoRunOptions? options = null,
            RecordingPublisher? publisher = null, TimeProvider? time = null)
        {
            var fixture = new Fixture(overlays, options ?? EnabledOptions(), publisher, time);
            await fixture.Service.StartAsync(CancellationToken.None);
            return fixture;
        }

        public async Task<AutoRunSummary> ReadyAsync(AutoRunPreviewRequest request)
        {
            var admitted = await Service.PreviewAsync(request, CancellationToken.None);
            return await WaitAsync(admitted.Id, AutoRunState.Ready);
        }

        public async Task<AutoRunSummary> WaitAsync(string id, AutoRunState state)
        {
            await UntilAsync(() =>
            {
                var current = Service.Get(id);
                if (current.State == AutoRunState.Failed && state != AutoRunState.Failed)
                    throw new InvalidOperationException(string.Join("; ", current.Errors));
                return current.State == state;
            });
            return Service.Get(id);
        }

        public async ValueTask DisposeAsync()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Service.StopAsync(timeout.Token);
            Service.Dispose();
        }
    }
}
