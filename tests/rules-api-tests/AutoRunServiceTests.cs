using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public async Task OverlayIdsWithoutTargetsRetainOldOverlaysWithoutResolvingRulesOrValidatingPipelineMetadata()
    {
        var overlay = Overlay("old", photoTime: "2000-01-01T00:00:00Z", imageUrl: "");
        var frozenPayload = overlay.Payload.ToArray();
        await using var fixture = await Fixture.StartAsync([overlay]);
        fixture.Resolver.Rules.Clear();
        var request = new AutoRunPreviewRequest
        {
            Source = new() { Mode = AutoRunSourceMode.OverlayIds, OverlayIds = ["old"] }
        };

        var ready = await fixture.ReadyAsync(request);

        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(ready.Targets);
        Assert.Empty(ready.Queries);
        Assert.Empty(fixture.Provider.SearchCursors);
        Assert.Equal(1, ready.MaxOverlays);
        Assert.Equal(1, ready.Counters.ObservedMatches);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Empty(Assert.Single(fixture.Service.Items(ready.Id, 0, 10, null).Items).Targets);
        Assert.Contains(ready.Warnings, warning => warning.Contains("Current active gateway rules", StringComparison.Ordinal)
            && warning.Contains("photo-age", StringComparison.Ordinal) && warning.Contains("not predicted", StringComparison.Ordinal));

        // Mutating the provider's original buffer cannot alter the frozen preview.
        Array.Fill(overlay.Payload, (byte)'x');
        request.Source.OverlayIds![0] = "changed";
        fixture.Service.Start(ready.Id);
        var complete = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);

        Assert.Equal(1, complete.Counters.Confirmed);
        var publication = Assert.Single(fixture.Publisher.Calls);
        Assert.Equal(frozenPayload, publication.Payload);
        Assert.Empty(publication.Targets);
        Assert.Equal(["old"], fixture.Provider.GetCalls);
    }

    [Theory]
    [InlineData("\"30200\"")]
    [InlineData("30200.5")]
    [InlineData("2147483648")]
    public async Task RejectedFindAirDimensionsIdentifyFieldsAndCannotProducePublishableSelection(string dimensionJson)
    {
        var overlay = Overlay("invalid-dimensions");
        var body = JsonNode.Parse(overlay.Payload)!;
        body["width"] = JsonNode.Parse(dimensionJson);
        body["height"] = JsonNode.Parse(dimensionJson);
        var payload = JsonSerializer.SerializeToUtf8Bytes(body);
        overlay = overlay with { Payload = payload, Input = GatewayInputMessageParser.Parse(payload) };
        await using var fixture = await Fixture.StartAsync([overlay]);

        var ready = await fixture.ReadyAsync(Request(["invalid-dimensions"], [Target("a") with { PipelineId = "FindAir" }]));

        Assert.Equal(1, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Invalid);
        Assert.Equal(0, ready.Counters.Selected);
        var error = Assert.Single(ready.Errors);
        Assert.Contains("Overlay 'invalid-dimensions' cannot enter pipeline 'FindAir'", error);
        Assert.Contains("input.width: Must be positive.", error);
        Assert.Contains("input.height: Must be positive.", error);
        Assert.Contains("input.width: Must be a 32-bit integer.", error);
        Assert.Contains("input.height: Must be a 32-bit integer.", error);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(ready.Id)).StatusCode);
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData(1, 1)]
    public async Task DirectOverlayQuotasChooseNewestThenStableIdAndDefaultToCappedUniqueIdCount(int? maximum, int selected)
    {
        var options = EnabledOptions();
        options.MaxSelectedOverlays = 2;
        await using var fixture = await Fixture.StartAsync(
            [Overlay("old", "2000-01-01T00:00:00Z"), Overlay("z"), Overlay("a")], options);
        var ready = await fixture.ReadyAsync(Request(["old", "z", "a", "z"], []) with { MaxOverlays = maximum });

        Assert.Equal(maximum ?? 2, ready.MaxOverlays);
        Assert.Equal(3, ready.Counters.Scanned);
        Assert.Equal(selected, ready.Counters.Selected);
        Assert.Equal(new[] { "a", "z" }.Take(selected), fixture.Service.Items(ready.Id, 0, 10, null).Items.Select(item => item.OverlayId));
        Assert.Equal(["old", "z", "a"], fixture.Provider.GetCalls);
        Assert.True(ready.SourceExhausted);
        Assert.Equal(0, fixture.Resolver.Calls);
    }

    [Fact]
    public async Task DirectIdsDeduplicateReturnedOverlaysCountMissingAndInvalidAndReportGlobalShortfalls()
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Provider.Get = (id, _) => Task.FromResult(id switch
        {
            "bad" => new ProviderOverlayResult(null, "Invalid overlay."),
            "missing" => new ProviderOverlayResult(null, "Missing overlay."),
            _ => new ProviderOverlayResult(Overlay(id == "alias" ? "z" : id), null)
        });
        var ready = await fixture.ReadyAsync(Request(["z", "alias", "z", "bad", "missing", "a"], []) with
        {
            MinOverlays = 4
        });

        Assert.Equal(5, ready.MaxOverlays);
        Assert.Equal(5, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Duplicates);
        Assert.Equal(2, ready.Counters.Invalid);
        Assert.Equal(2, ready.Counters.ObservedMatches);
        Assert.Equal(2, ready.Counters.Selected);
        Assert.Equal(2, ready.Shortfall);
        Assert.Equal(2, ready.Errors.Count);
        Assert.Contains(ready.Warnings, warning => warning.Contains("minimums", StringComparison.Ordinal));
        Assert.Empty(ready.Targets);
    }

    [Fact]
    public async Task RulesStillRequireTargets()
    {
        await using var fixture = await Fixture.StartAsync([]);
        var request = new AutoRunPreviewRequest { Source = new() { Mode = AutoRunSourceMode.Rules } };
        var error = await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(request, CancellationToken.None));

        Assert.Equal(400, error.StatusCode);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Provider.GetCalls);
        Assert.Empty(fixture.Provider.SearchCursors);
    }

    [Theory]
    [InlineData(100000, null, 100)]
    [InlineData(2, null, 2)]
    [InlineData(100000, 1, 1)]
    public async Task StandaloneFiltersUseBoundedGlobalQuotaWithoutResolvingRules(int serverMaximum, int? requestedMaximum, int expectedMaximum)
    {
        var options = EnabledOptions();
        options.MaxSelectedOverlays = serverMaximum;
        await using var fixture = await Fixture.StartAsync(
            [Overlay("old", "2000-01-01T00:00:00Z", ""), Overlay("z"), Overlay("a")], options);
        fixture.Resolver.Rules.Clear();
        var ready = await fixture.ReadyAsync(new()
        {
            Source = new() { Mode = AutoRunSourceMode.Filters }, MaxOverlays = requestedMaximum
        });

        Assert.Equal(expectedMaximum, ready.MaxOverlays);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Provider.GetCalls);
        Assert.Single(fixture.Provider.SearchCursors);
        Assert.Single(ready.Queries);
        Assert.Empty(ready.Targets);
        Assert.Equal(3, ready.Counters.ObservedMatches);
        Assert.Equal(Math.Min(3, expectedMaximum), ready.Counters.Selected);
        Assert.Equal(new[] { "a", "z", "old" }.Take(expectedMaximum),
            fixture.Service.Items(ready.Id, 0, 10, null).Items.Select(item => item.OverlayId));
        Assert.True(ready.SourceExhausted);
        Assert.Empty(fixture.Publisher.Calls);

        fixture.Service.Start(ready.Id);
        var completed = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        Assert.Equal(ready.Counters.Selected, completed.Counters.Confirmed);
        Assert.All(fixture.Publisher.Calls, publication => Assert.Empty(publication.Targets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandaloneFiltersRecheckCandidatesDeduplicateAndRespectSharedScanLimit(bool limited)
    {
        var options = EnabledOptions();
        if (limited) options.MaxScannedRecords = 3;
        await using var fixture = await Fixture.StartAsync([], options);
        fixture.Provider.Search = (_, next, _) => Task.FromResult(next is null
            ? new ProviderPage([new(Overlay("old", "2000-01-01T00:00:00Z"), null), new(Overlay("one"), null)], "/next")
            : new ProviderPage([new(Overlay("one"), null), new(Overlay("two"), null)], null));
        var ready = await fixture.ReadyAsync(new()
        {
            Source = new()
            {
                Mode = AutoRunSourceMode.Filters,
                Filters = new()
                {
                    SensorNames = ["camera"], Resolution = new() { Min = 0.7, Max = 0.7 },
                    PhotoTime = new() { From = "2020-01-01T00:00:00Z", To = "2020-01-03T00:00:00Z" }
                }
            }
        });

        Assert.Equal(limited ? 3 : 4, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Duplicates);
        Assert.Equal(limited ? 1 : 2, ready.Counters.Selected);
        Assert.Equal(limited ? "scanLimit" : null, ready.StoppedReason);
        Assert.Equal(!limited, ready.SourceExhausted);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.All(fixture.Service.Items(ready.Id, 0, 10, null).Items, item => Assert.NotEqual("old", item.OverlayId));
    }

    [Fact]
    public async Task RerunUsesFrozenCriteriaAndLatestSettingsButFetchesFreshPayloadsAndRules()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("original")]);
        var request = Request(["original"], [Target("a", 1, 2)]) with { MinOverlays = 1, MaxOverlays = 2 };
        var ready = await fixture.ReadyAsync(request);
        var settings = new AutoRunPublication { BatchSize = 1, DelayMs = 25 };
        fixture.Service.UpdatePublication(ready.Id, settings);
        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        request.Source.OverlayIds![0] = "mutated";
        request.Targets.Clear();
        fixture.Resolver.Rules["rule-a"].RuleName = "Updated rule";
        fixture.Resolver.Rules["rule-a"].RunParams = [RunParams("fresh-tenant")];
        fixture.Provider.Overlays["original"] = Overlay("original", imageUrl: "https://fresh.invalid/image");

        var admitted = await fixture.Service.RerunAsync(ready.Id, CancellationToken.None);
        var rerun = await fixture.WaitAsync(admitted.Id, AutoRunState.Ready);
        Assert.NotEqual(ready.Id, rerun.Id);
        Assert.Equal(settings, rerun.Publication);
        Assert.Equal(1, rerun.MinOverlays);
        Assert.Equal(2, rerun.MaxOverlays);
        Assert.Equal("Updated rule", Assert.Single(rerun.Targets).RuleName);
        Assert.Equal(1, rerun.Targets[0].MinOverlays);
        Assert.Equal(2, rerun.Targets[0].MaxOverlays);
        Assert.Equal(2, fixture.Resolver.Calls);
        Assert.Equal(["original", "original"], fixture.Provider.GetCalls);
        Assert.Single(fixture.Publisher.Calls); // Rerun prepares; it never starts publication.
        Assert.Equal(AutoRunState.Completed, fixture.Service.Get(ready.Id).State);
        Assert.Equal("rule-a", fixture.Service.Get(ready.Id).Targets[0].RuleName);

        fixture.Service.Start(rerun.Id);
        await fixture.WaitAsync(rerun.Id, AutoRunState.Completed);
        var calls = fixture.Publisher.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.NotEqual(calls[0].RunId, calls[1].RunId);
        using var payload = JsonDocument.Parse(calls[1].Payload);
        Assert.Equal("https://fresh.invalid/image", payload.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("fresh-tenant", calls[1].Targets[0].Rule.RunParams![0].GetProperty("tenantId").GetString());
    }

    [Fact]
    public async Task RerunOfFiltersPreservesOriginalFiltersWithoutRuleResolution()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("old", "2000-01-01T00:00:00Z"), Overlay("one")]);
        var filters = new AutoRunFilters { SensorNames = ["camera"], PhotoTime = new() { From = "2020-01-01T00:00:00Z" } };
        var ready = await fixture.ReadyAsync(new() { Source = new() { Mode = AutoRunSourceMode.Filters, Filters = filters } });
        fixture.Service.Cancel(ready.Id);
        filters.SensorNames![0] = "changed";
        fixture.Provider.Overlays["two"] = Overlay("two");

        var created = await fixture.Service.RerunAsync(ready.Id, CancellationToken.None);
        var rerun = await fixture.WaitAsync(created.Id, AutoRunState.Ready);
        Assert.Equal(2, rerun.Counters.Selected);
        Assert.Equal(ready.Queries[0].GetRawText(), rerun.Queries[0].GetRawText());
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(rerun.Targets);
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task RerunRejectsActiveOrMissingJobsWithoutAdmittingWork()
    {
        var blocked = new TaskCompletionSource<ProviderOverlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        fixture.Provider.Get = (_, token) => blocked.Task.WaitAsync(token);
        var preparing = await fixture.Service.PreviewAsync(Request(["one"], []), CancellationToken.None);

        foreach (var id in new[] { ready.Id, preparing.Id })
            Assert.Equal(409, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
                fixture.Service.RerunAsync(id, CancellationToken.None))).StatusCode);
        Assert.Equal(404, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.RerunAsync("missing", CancellationToken.None))).StatusCode);
        Assert.Equal(2, fixture.Service.List().Count);
        Assert.Empty(fixture.Publisher.Calls);
        fixture.Service.Cancel(preparing.Id);
        await fixture.WaitAsync(preparing.Id, AutoRunState.Cancelled);
    }

    [Fact]
    public async Task DirectIdsStillValidateIdsOverallLimitsFiltersAndNullTargets()
    {
        var options = EnabledOptions();
        options.MaxSelectedOverlays = 2;
        await using var fixture = await Fixture.StartAsync([], options);
        var invalidRequests = new[]
        {
            Request([], []), Request([" "], []), Request([null!], []),
            Request(["one"], []) with { MinOverlays = 2 },
            Request(["one"], []) with { MaxOverlays = 0 },
            Request(["one"], []) with { MaxOverlays = 3 },
            Request(["one"], []) with { MinOverlays = -1 },
            Request(["one"], []) with { Targets = null! },
            Request(["one"], []) with
            {
                Source = new() { Mode = AutoRunSourceMode.OverlayIds, OverlayIds = ["one"], Filters = new() }
            }
        };

        foreach (var request in invalidRequests)
            Assert.Equal(400, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
                fixture.Service.PreviewAsync(request, CancellationToken.None))).StatusCode);
        Assert.Equal(0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Provider.GetCalls);
    }

    [Theory]
    [InlineData("scanLimit", false)]
    [InlineData("candidateLimit", false)]
    [InlineData("scanLimit", true)]
    [InlineData("candidateLimit", true)]
    public async Task PreparationBoundsScanningAndCandidateRetentionAndReportsPartialSource(string limit, bool direct)
    {
        var options = EnabledOptions();
        if (limit == "scanLimit") options.MaxScannedRecords = 2;
        else options.MaxCandidates = 2;
        await using var fixture = await Fixture.StartAsync(
            [Overlay("one"), Overlay("two"), Overlay("three")], options);
        var ready = await fixture.ReadyAsync(Request(["one", "two", "three"], direct ? [] : null));

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
    public async Task MultipleRuleSearchesFinishAllPagesBeforeStartingTheNextQuery()
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
        Assert.Equal([0, 0, 1, 1], requests.Select(request => request.Branch));
        Assert.Equal([null, "/branch-0-next", null, "/branch-1-next"], requests.Select(request => request.Cursor));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ByteBudgetStopPreservesCandidatesForPublicationAndAllowsLaterPreview(bool direct)
    {
        var oversized = Overlay("oversized", imageUrl: "https://example.invalid/" + new string('a', 8000));
        var options = EnabledOptions();
        options.PayloadBudgetBytes = 5000;
        await using var fixture = await Fixture.StartAsync([Overlay("small"), oversized], options);
        var first = await fixture.Service.PreviewAsync(Request(["small", "oversized"], direct ? [] : null), CancellationToken.None);
        var ready = await fixture.WaitAsync(first.Id, AutoRunState.Ready);

        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal("payloadBudget", ready.StoppedReason);
        Assert.False(ready.SourceExhausted);
        Assert.Contains(ready.Warnings, warning => warning.Contains("payload budget", StringComparison.OrdinalIgnoreCase));
        fixture.Service.Start(first.Id);
        await fixture.WaitAsync(first.Id, AutoRunState.Completed);
        Assert.Equal("small", Assert.Single(fixture.Publisher.Calls).OverlayId);
        var next = await fixture.ReadyAsync(Request(["small"], direct ? [] : null));
        Assert.Equal(1, next.Counters.Selected);
        Assert.Single(fixture.Publisher.Calls);
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
        var partial = await fixture.WaitAsync(second.Id, AutoRunState.Ready);
        Assert.Equal("payloadBudget", partial.StoppedReason);
        Assert.Equal(0, partial.Counters.Selected);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(second.Id)).StatusCode);
        Assert.Equal(AutoRunState.Cancelled, fixture.Service.Cancel(first.Id).State);

        var afterRelease = await fixture.ReadyAsync(Request(["large"]));
        Assert.Equal(1, afterRelease.Counters.Selected);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MemoryPressureDuringAdmissionDoesNotRetainAnUnrunnableJob(bool duringRuleResolution)
    {
        var memory = new AutoRunMemoryTestGuard { Blocked = !duringRuleResolution };
        await using var fixture = await Fixture.StartAsync([], memory: memory);
        fixture.Resolver.OnResolved = () => memory.Blocked = true;

        var error = await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(Request(["one"]), CancellationToken.None));

        Assert.Equal(409, error.StatusCode);
        Assert.Contains("Memory", error.Message, StringComparison.Ordinal);
        Assert.Equal(duringRuleResolution ? 1 : 0, fixture.Resolver.Calls);
        Assert.Empty(fixture.Provider.GetCalls);
        Assert.Empty(fixture.Service.List());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProcessMemoryPressureStopsPreparationButCollectedOverlaysCanRun(bool direct)
    {
        var memory = new AutoRunMemoryTestGuard();
        await using var fixture = await Fixture.StartAsync([], memory: memory);
        fixture.Provider.Get = (id, _) =>
        {
            if (id == "two") memory.Blocked = true;
            return Task.FromResult(new ProviderOverlayResult(Overlay(id), null));
        };
        var ready = await fixture.ReadyAsync(Request(["one", "two", "three"], direct ? [] : null));

        Assert.Equal("memoryLimit", ready.StoppedReason);
        Assert.False(ready.SourceExhausted);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.True(ready.Memory!.IsUnderPressure);
        Assert.Equal(["one", "two"], fixture.Provider.GetCalls);
        Assert.Empty(ready.Errors);
        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        Assert.Equal("one", Assert.Single(fixture.Publisher.Calls).OverlayId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ResourceLimitOnLaterPageKeepsTheEarlierSelection(bool responseLimit)
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Provider.Search = (_, next, _) => next is null
            ? Task.FromResult(new ProviderPage([new(Overlay("one"), null)], "/next"))
            : throw (responseLimit ? new ProviderResponseLimitException(1000) : new AutoRunMemoryLimitException());
        var ready = await fixture.ReadyAsync(Request([], []) with { Source = new() { Mode = AutoRunSourceMode.Filters } });

        Assert.Equal(responseLimit ? "responseLimit" : "memoryLimit", ready.StoppedReason);
        Assert.False(ready.SourceExhausted);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.NotEmpty(ready.Warnings);
        Assert.Empty(ready.Errors);
        fixture.Service.Start(ready.Id);
        await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        Assert.Single(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task ResourceLimitBeforeAnyResultsProducesHonestEmptyPreview()
    {
        await using var fixture = await Fixture.StartAsync([]);
        fixture.Provider.Search = (_, _, _) => throw new ProviderResponseLimitException(1000);
        var ready = await fixture.ReadyAsync(Request([], []) with { Source = new() { Mode = AutoRunSourceMode.Filters }, MinOverlays = 1 });
        Assert.Equal("responseLimit", ready.StoppedReason);
        Assert.Equal(0, ready.Counters.Selected);
        Assert.False(ready.SourceExhausted);
        Assert.Equal(1, ready.Shortfall);
        Assert.Contains(ready.Warnings, warning => warning.Contains("No selected overlays are available to submit.", StringComparison.Ordinal));
        Assert.DoesNotContain(ready.Warnings, warning => warning.Contains("can still be submitted", StringComparison.Ordinal) ||
            warning.Contains("can be started", StringComparison.Ordinal));
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(ready.Id)).StatusCode);
        Assert.Empty(fixture.Publisher.Calls);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationInterruptsInterBatchDelayWithoutSubmittingNextItem(bool direct)
    {
        var time = new ControlledDelayTimeProvider();
        await using var fixture = await Fixture.StartAsync([Overlay("first"), Overlay("second")], time: time);
        var ready = await fixture.ReadyAsync(Request(["first", "second"], direct ? [] : null) with
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelPreparingReleasesAdmissionAndReadyCancellationDoesNotPublish(bool direct)
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.Get = async (_, token) =>
        {
            requested.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new(Overlay("one"), null);
        };
        var first = await fixture.Service.PreviewAsync(Request(["one"], direct ? [] : null), CancellationToken.None);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var concurrent = await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(Request(["one"], direct ? [] : null), CancellationToken.None));
        Assert.Equal(409, concurrent.StatusCode);
        fixture.Service.Cancel(first.Id);
        await fixture.WaitAsync(first.Id, AutoRunState.Cancelled);
        fixture.Provider.Get = null;

        var second = await fixture.ReadyAsync(Request(["one"], direct ? [] : null));
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

    [Fact]
    public async Task DeletingReadyPreviewReleasesItsAccountedBytesAndJobCapacity()
    {
        var options = EnabledOptions();
        options.MaxRetainedJobs = 1;
        await using var fixture = await Fixture.StartAsync([Overlay("one")], options);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        Assert.True(ready.RetainedBytes > Overlay("one").Payload.Length);
        Assert.False(ready.DeletionRequested);
        options.PayloadBudgetBytes = ready.RetainedBytes;
        Assert.Equal(409, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.PreviewAsync(Request(["one"], []), CancellationToken.None))).StatusCode);

        var deleted = fixture.Service.Delete(ready.Id);
        Assert.Contains(ready.Id, deleted.DeletedIds.Concat(deleted.PendingIds));
        if (deleted.PendingIds.Count == 0) Assert.Equal(ready.RetainedBytes, deleted.ReleasedRetainedBytes);
        await UntilAsync(() => fixture.Service.List().Count == 0);
        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Get(ready.Id)).StatusCode);
        Assert.Equal(404, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Delete(ready.Id)).StatusCode);
        var next = await fixture.ReadyAsync(Request(["one"], []));
        Assert.Equal(1, next.Counters.Selected);
        Assert.Equal(ready.RetainedBytes, next.RetainedBytes);
    }

    [Fact]
    public async Task TerminalWeightsReleasePayloadsAndBulkDeletionReleasesRemainingData()
    {
        var overlay = Overlay("one");
        await using var fixture = await Fixture.StartAsync([overlay]);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        fixture.Service.Start(ready.Id);
        var completed = await fixture.WaitAsync(ready.Id, AutoRunState.Completed);
        Assert.Equal(overlay.Payload.Length, ready.RetainedBytes - completed.RetainedBytes);
        Assert.True(completed.RetainedBytes > 0);
        var second = await fixture.ReadyAsync(Request(["one"], []));

        var deleted = fixture.Service.DeleteAll();
        Assert.Equal(new[] { ready.Id, second.Id }.Order(), deleted.DeletedIds.Concat(deleted.PendingIds).Order());
        if (deleted.PendingIds.Count == 0)
            Assert.Equal(completed.RetainedBytes + second.RetainedBytes, deleted.ReleasedRetainedBytes);
        await UntilAsync(() => fixture.Service.List().Count == 0);
        Assert.Single(fixture.Publisher.Calls); // Removing retained data never recalls or repeats a submission.
        var empty = fixture.Service.DeleteAll();
        Assert.Empty(empty.DeletedIds);
        Assert.Empty(empty.PendingIds);
        Assert.Equal(0, empty.ReleasedRetainedBytes);
    }

    [Fact]
    public async Task DeletingQueuedPreparationWaitsForWorkerAndRejectsMutations()
    {
        await using var fixture = new Fixture([Overlay("one")], EnabledOptions(), null, null, null);
        var preparing = await fixture.Service.PreviewAsync(Request(["one"], []), CancellationToken.None);
        var result = fixture.Service.Delete(preparing.Id);
        Assert.Empty(result.DeletedIds);
        Assert.Equal([preparing.Id], result.PendingIds);
        Assert.Equal(0, result.ReleasedRetainedBytes);
        Assert.True(fixture.Service.Get(preparing.Id).DeletionRequested);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.Start(preparing.Id)).StatusCode);
        Assert.Equal(409, Assert.Throws<AutoRunRequestException>(() => fixture.Service.UpdatePublication(preparing.Id, new())).StatusCode);
        Assert.Equal(409, (await Assert.ThrowsAsync<AutoRunRequestException>(() =>
            fixture.Service.RerunAsync(preparing.Id, CancellationToken.None))).StatusCode);
        Assert.Equal([preparing.Id], fixture.Service.Delete(preparing.Id).PendingIds);

        await fixture.Service.StartAsync(CancellationToken.None);
        await UntilAsync(() => fixture.Service.List().Count == 0);
        Assert.Empty(fixture.Provider.GetCalls);
        Assert.Empty(fixture.Publisher.Calls);
        Assert.Equal(1, (await fixture.ReadyAsync(Request(["one"], []))).Counters.Selected);
    }

    [Fact]
    public async Task BulkDeletionCancelsPreparingWorkAndWaitsForProviderBeforeReleasingIt()
    {
        await using var fixture = await Fixture.StartAsync([Overlay("one")]);
        var ready = await fixture.ReadyAsync(Request(["one"], []));
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<ProviderOverlayResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken providerToken = default;
        fixture.Provider.Get = (_, token) => { providerToken = token; requested.TrySetResult(); return response.Task; };
        var preparing = await fixture.Service.PreviewAsync(Request(["one", "two"], []), CancellationToken.None);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deleted = fixture.Service.DeleteAll();
        Assert.Equal([ready.Id], deleted.DeletedIds);
        Assert.Equal(ready.RetainedBytes, deleted.ReleasedRetainedBytes);
        Assert.Equal([preparing.Id], deleted.PendingIds);
        Assert.True(providerToken.IsCancellationRequested);
        var pending = Assert.Single(fixture.Service.List());
        Assert.True(pending.DeletionRequested);
        Assert.True(pending.RetainedBytes > 0);
        response.SetResult(new(Overlay("one"), null));
        await UntilAsync(() => fixture.Service.List().Count == 0);
        Assert.Equal(["one", "one"], fixture.Provider.GetCalls);
        Assert.Empty(fixture.Publisher.Calls);
    }

    [Fact]
    public async Task DeletingPublishingRunWaitsForInflightOutcomeAndNeverSendsLaterItems()
    {
        var response = new TaskCompletionSource<AutoRunPublishResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken publicationToken = default;
        var publisher = new RecordingPublisher((_, token) =>
        {
            publicationToken = token;
            requested.TrySetResult();
            return response.Task;
        });
        await using var fixture = await Fixture.StartAsync([Overlay("one"), Overlay("two")], publisher: publisher);
        var ready = await fixture.ReadyAsync(Request(["one", "two"], []) with { Publication = new() { BatchSize = 1, DelayMs = 0 } });
        fixture.Service.Start(ready.Id);
        await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        var deleted = fixture.Service.Delete(ready.Id);
        Assert.Equal([ready.Id], deleted.PendingIds);
        Assert.Empty(deleted.DeletedIds);
        Assert.True(publicationToken.IsCancellationRequested);
        var pending = fixture.Service.Get(ready.Id);
        Assert.True(pending.DeletionRequested);
        Assert.Equal(1, pending.Counters.InFlight);
        Assert.Equal(ready.RetainedBytes, pending.RetainedBytes);
        response.SetResult(new(AutoRunItemOutcome.Confirmed));
        await UntilAsync(() => fixture.Service.List().Count == 0);
        Assert.Single(publisher.Calls);
        Assert.Equal(0, publisher.Active);
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
        public Action? OnResolved { get; set; }
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
            OnResolved?.Invoke();
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
        public Fixture(ProviderOverlay[] overlays, AutoRunOptions options, RecordingPublisher? publisher, TimeProvider? time,
            IAutoRunMemoryGuard? memory)
        {
            Resolver = new();
            Provider = new(overlays);
            Publisher = publisher ?? new();
            Service = new(Resolver, Provider, new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions())),
                Publisher, Options.Create(options), time ?? TimeProvider.System, NullLogger<AutoRunService>.Instance, memory);
        }

        public FakeResolver Resolver { get; }
        public FakeProvider Provider { get; }
        public RecordingPublisher Publisher { get; }
        public AutoRunService Service { get; }

        public static async Task<Fixture> StartAsync(
            ProviderOverlay[] overlays, AutoRunOptions? options = null,
            RecordingPublisher? publisher = null, TimeProvider? time = null, IAutoRunMemoryGuard? memory = null)
        {
            var fixture = new Fixture(overlays, options ?? EnabledOptions(), publisher, time, memory);
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
