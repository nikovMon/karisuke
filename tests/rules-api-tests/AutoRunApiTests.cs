using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunApiTests
{
    private const string RulesIndex = "asd-integ-pipeline-index";
    private const string OtherPipelineIndex = "algo-integ-pipeline-index";
    private const string Area = "POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0))";

    [Theory]
    [InlineData(AutoRunSourceMode.Rules, false)]
    [InlineData(AutoRunSourceMode.OverlayIds, false)]
    [InlineData(AutoRunSourceMode.Filters, true)]
    [InlineData(AutoRunSourceMode.OverlayIds, true)]
    public async Task WholeDecimalProviderDimensionsPrepareAndPublishAsGatewayIntegers(AutoRunSourceMode mode, bool direct)
    {
        using var fixture = new Fixture();
        using var width = JsonDocument.Parse("8836.0");
        using var height = JsonDocument.Parse("3608.0");
        fixture.Provider.ImageWidth = width.RootElement;
        fixture.Provider.ImageHeight = height.RootElement;
        if (!direct) fixture.Store.Add(RulesIndex, "shared-rule", Rule("Dimension test", "tenant"));
        var request = Request() with
        {
            Source = new() { Mode = mode, OverlayIds = mode == AutoRunSourceMode.OverlayIds ? ["overlay-1"] : null },
            Targets = direct ? [] : Request().Targets
        };

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var created = await Read<AutoRunSummary>(accepted);
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(0, ready.Counters.Invalid);
        Assert.Empty(ready.Errors);

        using var started = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        var completed = await WaitForState(fixture.Client, created.Id, AutoRunState.Completed);
        Assert.Equal(1, completed.Counters.Confirmed);
        using var message = JsonDocument.Parse(Assert.Single(fixture.Broker.Messages).Message.Body);
        var overlay = message.RootElement;
        AssertOrdinaryGatewayInput(Assert.Single(fixture.Broker.Messages).Message, created.Id);
        Assert.Equal("8836", overlay.GetProperty("width").GetRawText());
        Assert.Equal("3608", overlay.GetProperty("height").GetRawText());
        var gatewayInput = GatewayInputMessageParser.Parse(Encoding.UTF8.GetBytes(overlay.GetRawText()));
        Assert.Equal(8836, gatewayInput.ImageWidth);
        Assert.Equal(3608, gatewayInput.ImageHeight);
    }

    [Theory]
    [InlineData(AutoRunSourceMode.Rules)]
    [InlineData(AutoRunSourceMode.Filters)]
    public async Task PreviewFreezesSelectionDataButPublishesOnlyOrdinaryGatewayInput(AutoRunSourceMode mode)
    {
        using var fixture = new Fixture();
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Original inactive rule", "original-tenant"));
        fixture.Store.Add(OtherPipelineIndex, "shared-rule", Rule("Other pipeline rule", "other-tenant"));
        var request = Request() with
        {
            Source = new()
            {
                Mode = mode,
                Filters = new() { IsRealtime = false, Resolution = new() { Min = 0.5, Max = 0.5 } }
            },
            Publication = new() { BatchSize = 1, DelayMs = 0 }
        };

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var created = await Read<AutoRunSummary>(accepted);
        Assert.EndsWith($"/auto-runs/{created.Id}", accepted.Headers.Location!.ToString());
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);

        Assert.Equal(1, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(0, ready.Counters.Confirmed);
        Assert.Equal(0, ready.Shortfall);
        Assert.True(ready.SourceExhausted);
        var target = Assert.Single(ready.Targets);
        Assert.Equal("FindAir", target.PipelineId);
        Assert.Equal("shared-rule", target.RuleId);
        Assert.Equal("Original inactive rule", target.RuleName);
        Assert.False(target.IsActive);
        Assert.Null(target.Rule.Match!.MaxPhotoAgeDays);
        Assert.Empty(fixture.Broker.Messages);

        var providerCalls = fixture.Provider.Calls.Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, providerCalls.Length);
        Assert.Contains("result_type=4", Assert.Single(fixture.Provider.Calls, call => call.Method == HttpMethod.Get).Uri.Query);
        Assert.All(providerCalls, call => Assert.Equal(HttpMethod.Post, call.Method));
        Assert.Equal(providerCalls[0].Body, providerCalls[1].Body);
        Assert.Equal("?searchAfter=next&to=1000", providerCalls[1].Uri.Query);
        using (var query = JsonDocument.Parse(providerCalls[0].Body!))
        {
            var clauses = query.RootElement.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND")
                .EnumerateArray().ToArray();
            var realtime = clauses.Single(c => c.TryGetProperty("match", out var match) && match.TryGetProperty("ISREALTIME", out _))
                .GetProperty("match").GetProperty("ISREALTIME");
            Assert.Equal("IN", realtime.GetProperty("type").GetString());
            Assert.False(realtime.GetProperty("values")[0].GetBoolean());
            Assert.Equal(mode == AutoRunSourceMode.Rules,
                clauses.Any(c => c.TryGetProperty("match", out var match) && match.TryGetProperty("SENSOR", out _)));
        }

        using var itemsResponse = await fixture.Client.GetAsync($"/auto-runs/{created.Id}/items?from=0&size=1&outcome=Pending");
        await AssertStatus(itemsResponse, HttpStatusCode.OK);
        var items = await Read<AutoRunItemsPage>(itemsResponse);
        Assert.Equal(1, items.Total);
        Assert.Equal("overlay-1", Assert.Single(items.Items).OverlayId);
        Assert.Equal("shared-rule", Assert.Single(items.Items[0].Targets).RuleId);
        Assert.Equal(DateTimeOffset.Parse("2000-01-02T10:00:00Z"), items.Items[0].PhotoTime);

        // Editing upstream sources after Ready cannot alter the selection or overlay payload.
        // Gateway routing remains independent and uses its current active rules.
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Changed rule", "changed-tenant"));
        fixture.Provider.ImageUrl = "https://images.invalid/changed";
        using var started = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        var completed = await WaitForState(fixture.Client, created.Id, AutoRunState.Completed);
        Assert.Equal(1, completed.Counters.Confirmed);
        Assert.Equal(0, completed.Counters.Failed);
        Assert.Equal(0, completed.Counters.Uncertain);
        Assert.Equal(0, completed.Counters.Remaining);
        Assert.Equal(1, Assert.Single(completed.Targets).Confirmed);

        var published = Assert.Single(fixture.Broker.Messages);
        Assert.Equal("unified-gateway.input", published.Destination.QueueName);
        Assert.Equal("broker.invalid", published.Destination.Connection.Host);
        Assert.Equal(created.Id, published.Message.CorrelationId);
        Assert.Equal("application/json", published.Message.ContentType);
        Assert.Equal(64, published.Message.MessageId.Length);
        AssertOrdinaryGatewayInput(published.Message, created.Id);
        var frozenTarget = Assert.Single(completed.Targets);
        Assert.Equal("Original inactive rule", frozenTarget.Rule.RuleName);
        Assert.False(frozenTarget.Rule.IsActive);
        Assert.Equal("original-tenant", frozenTarget.Rule.RunParams![0].GetProperty("tenantId").GetString());
        using var body = JsonDocument.Parse(published.Message.Body);
        var overlay = body.RootElement;
        Assert.Equal("overlay-1", overlay.GetProperty("id").GetString());
        Assert.Equal("https://images.invalid/original", overlay.GetProperty("imageUrl").GetString());
        Assert.Equal("https://grids.invalid/original", overlay.GetProperty("gridURI").GetString());
        Assert.Equal("camera", overlay.GetProperty("sensorName").GetString());
        Assert.Equal("EO", overlay.GetProperty("sensorType").GetString());
        Assert.Equal("grid", overlay.GetProperty("gridType").GetString());
        Assert.Equal("sortie-1", overlay.GetProperty("sortieId").GetString());
        Assert.Equal(0.5, overlay.GetProperty("bestResolution").GetDouble());
        Assert.Equal("Accurate", overlay.GetProperty("registrationQuality").GetString());
        Assert.Equal(100, overlay.GetProperty("width").GetInt32());
        Assert.Equal(50, overlay.GetProperty("height").GetInt32());
        Assert.Equal("Polygon", overlay.GetProperty("roiFootprint").GetProperty("type").GetString());
        Assert.False(overlay.GetProperty("isRealtime").GetBoolean());
        Assert.Equal(DateTimeOffset.Parse("2000-01-02T10:00:00Z"), overlay.GetProperty("photoTime").GetDateTimeOffset());
        Assert.Equal(3, fixture.Provider.Calls.Count);

        using var repeatedStart = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(repeatedStart, HttpStatusCode.OK);
        Assert.Equal(AutoRunState.Completed, (await Read<AutoRunSummary>(repeatedStart)).State);
        Assert.Single(fixture.Broker.Messages);
        using var confirmedItems = await fixture.Client.GetAsync($"/auto-runs/{created.Id}/items?outcome=Confirmed");
        Assert.Equal(AutoRunItemOutcome.Confirmed, Assert.Single((await Read<AutoRunItemsPage>(confirmedItems)).Items).Outcome);
        using var history = await fixture.Client.GetAsync("/auto-runs");
        Assert.Equal(created.Id, Assert.Single(await Read<List<AutoRunSummary>>(history)).Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OverlayIdsWithoutRulesPublishesFrozenOrdinaryGatewayInput(bool includeEmptyTargets)
    {
        using var fixture = new Fixture();
        // No rules exist in this store. Direct submission must not require a rule lookup.
        var request = new Dictionary<string, object>
        {
            ["source"] = new { mode = "OverlayIds", overlayIds = new[] { "overlay-1", "overlay-1" } },
            ["publication"] = new { batchSize = 1, delayMs = 0 }
        };
        if (includeEmptyTargets) request["targets"] = Array.Empty<object>();
        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var created = await Read<AutoRunSummary>(accepted);
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);

        Assert.Empty(ready.Targets);
        Assert.Empty(ready.Queries);
        Assert.Equal(1, ready.MaxOverlays);
        Assert.Equal(1, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.True(ready.SourceExhausted);
        Assert.NotEmpty(ready.Warnings);
        Assert.Equal(2, fixture.Provider.Calls.Count);
        Assert.All(fixture.Provider.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
        Assert.Empty(fixture.Broker.Messages);
        using var itemsResponse = await fixture.Client.GetAsync($"/auto-runs/{created.Id}/items");
        var item = Assert.Single((await Read<AutoRunItemsPage>(itemsResponse)).Items);
        Assert.Empty(item.Targets);

        fixture.Provider.ImageUrl = "https://images.invalid/changed-after-preview";
        using var started = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        var completed = await WaitForState(fixture.Client, created.Id, AutoRunState.Completed);
        Assert.Equal(1, completed.Counters.Confirmed);
        Assert.Empty(completed.Targets);
        var published = Assert.Single(fixture.Broker.Messages);
        Assert.Equal("unified-gateway.input", published.Destination.QueueName);
        Assert.Equal(created.Id, published.Message.CorrelationId);
        var input = GatewayInputMessageParser.Parse(published.Message.Body);
        Assert.Equal("overlay-1", input.ImageId);
        Assert.Equal("https://images.invalid/original", input.ImageUrl);
        Assert.Equal(DateTimeOffset.Parse("2000-01-02T10:00:00Z"), input.PhotoTime);
        Assert.Equal("Polygon", input.Geometry.GeometryType);
        using var body = JsonDocument.Parse(published.Message.Body);
        Assert.Equal("overlay-1", body.RootElement.GetProperty("id").GetString());
        foreach (var name in new[] { "kind", "version", "runId", "overlay", "targets" })
            Assert.False(body.RootElement.TryGetProperty(name, out _));
        Assert.NotNull(published.Message.Headers);
        var updatedFields = Assert.IsAssignableFrom<IEnumerable<object>>(published.Message.Headers["x-updated-fields"]);
        Assert.Contains("gridType", updatedFields);
        using var repeatedStart = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(repeatedStart, HttpStatusCode.OK);
        Assert.Single(fixture.Broker.Messages);
    }

    [Theory]
    [InlineData(AutoRunSourceMode.Rules, 0, 1)]
    [InlineData(AutoRunSourceMode.Rules, 3, 0)]
    [InlineData(AutoRunSourceMode.OverlayIds, 0, 1)]
    [InlineData(AutoRunSourceMode.OverlayIds, 3, 0)]
    public async Task AgeOnlyRulesRetainTheirLimitDuringSelection(
        AutoRunSourceMode mode, int photoAgeDays, int expectedSelected)
    {
        var clock = new MutableTimeProvider(DateTimeOffset.Parse("2026-10-27T10:00:00Z"));
        using var fixture = new Fixture(time: clock);
        fixture.Provider.PhotoTime = clock.GetUtcNow().AddDays(-photoAgeDays).ToString("O");
        var rule = Rule("One-day limit", "tenant");
        rule.IsActive = true;
        rule.Match = new() { MaxPhotoAgeDays = 1 };
        fixture.Store.Add(RulesIndex, "shared-rule", rule);
        var request = Request() with
        {
            Source = new() { Mode = mode, OverlayIds = mode == AutoRunSourceMode.OverlayIds ? ["overlay-1"] : null }
        };

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var ready = await WaitForState(fixture.Client, (await Read<AutoRunSummary>(accepted)).Id, AutoRunState.Ready);

        Assert.Equal(expectedSelected, ready.Counters.Selected);
        var target = Assert.Single(ready.Targets);
        Assert.Equal(1, target.Rule.Match!.MaxPhotoAgeDays);
        Assert.Equal(expectedSelected, target.Selected);
        Assert.Equal(1 - expectedSelected, target.Shortfall);
        Assert.Contains(ready.Warnings, warning => warning.Contains("photo-age", StringComparison.Ordinal));
        var searches = fixture.Provider.Calls.Where(call => call.Method == HttpMethod.Post).ToArray();
        if (mode == AutoRunSourceMode.Rules)
        {
            var frozenQuery = Assert.Single(ready.Queries);
            Assert.Equal(2, searches.Length);
            Assert.All(searches, call => Assert.Equal(frozenQuery.GetRawText(), call.Body));
            Assert.False(searches[0].Uri.Query.Contains("searchAfter", StringComparison.Ordinal));
            Assert.Contains("searchAfter=next", searches[1].Uri.Query);
            var dateRange = Assert.Single(frozenQuery.GetProperty("filter").GetProperty("logical_operators")
                .GetProperty("AND").EnumerateArray()).GetProperty("range").GetProperty("date");
            Assert.Equal("IN", dateRange.GetProperty("type").GetString());
            Assert.Equal("2026-10-26T10:00:00.000Z", dateRange.GetProperty("values")[0].GetProperty("gte").GetString());
        }
        else
        {
            Assert.Empty(ready.Queries);
            Assert.Empty(searches);
            Assert.All(fixture.Provider.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));
        }
        using var started = await fixture.Client.PostAsync($"/auto-runs/{ready.Id}/start", null);
        if (expectedSelected == 0)
        {
            await AssertStatus(started, HttpStatusCode.Conflict);
            Assert.Empty(fixture.Broker.Messages);
        }
        else
        {
            await AssertStatus(started, HttpStatusCode.OK);
            await WaitForState(fixture.Client, ready.Id, AutoRunState.Completed);
            AssertOrdinaryGatewayInput(Assert.Single(fixture.Broker.Messages).Message, ready.Id);
        }
    }

    [Fact]
    public async Task OverlappingRulesApplyTheirOwnAgeLimitsAndNoLimitRemainsUnrestricted()
    {
        var referenceTime = DateTimeOffset.Parse("2026-10-27T10:00:00Z");
        var clock = new MutableTimeProvider(referenceTime);
        using var fixture = new Fixture(time: clock,
            configurationOverrides: new Dictionary<string, string?> { ["AutoRun:Provider:RequestFields:PhotoTime"] = "DATE" });
        fixture.Provider.PhotoTime = referenceTime.AddDays(-3).ToString("O");
        // Time advances while paging; all branches must keep their original cutoff and body.
        fixture.Provider.BeforeResponse = _ => { clock.Advance(TimeSpan.FromMinutes(1)); return Task.CompletedTask; };
        foreach (var (id, age) in new (string, int?)[] { ("one-day", 1), ("seven-days", 7), ("same-seven-days", 7), ("no-limit", null) })
        {
            var rule = Rule(id, "tenant");
            rule.IsActive = true;
            rule.Match = age is null ? null : new() { MaxPhotoAgeDays = age };
            rule.MatchAll = age is null;
            fixture.Store.Add(RulesIndex, id, rule);
        }
        var request = Request() with
        {
            Source = new() { Mode = AutoRunSourceMode.Rules },
            Targets = new[] { "one-day", "seven-days", "same-seven-days", "no-limit" }
                .Select(id => new AutoRunTarget { PipelineId = "FindAir", RuleId = id, MinOverlays = 1, MaxOverlays = 1 }).ToList()
        };

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var ready = await WaitForState(fixture.Client, (await Read<AutoRunSummary>(accepted)).Id, AutoRunState.Ready);

        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(0, ready.Targets.Single(target => target.RuleId == "one-day").Selected);
        Assert.Equal(1, ready.Targets.Single(target => target.RuleId == "seven-days").Selected);
        Assert.Equal(1, ready.Targets.Single(target => target.RuleId == "same-seven-days").Selected);
        Assert.Equal(1, ready.Targets.Single(target => target.RuleId == "no-limit").Selected);
        Assert.Equal(7, ready.Targets.Single(target => target.RuleId == "seven-days").Rule.Match!.MaxPhotoAgeDays);
        Assert.Equal(3, ready.Queries.Count); // Different ages stay separate; equal age branches deduplicate.
        var searches = fixture.Provider.Calls.Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(6, searches.Length);
        for (var i = 0; i < ready.Queries.Count; i++)
        {
            Assert.Equal(ready.Queries[i].GetRawText(), searches[i * 2].Body);
            Assert.Equal(searches[i * 2].Body, searches[i * 2 + 1].Body);
            Assert.DoesNotContain("searchAfter", searches[i * 2].Uri.Query, StringComparison.Ordinal);
            Assert.Contains("searchAfter=next", searches[i * 2 + 1].Uri.Query);
        }
        var cutoffs = ready.Queries.Take(2).Select(query => Assert.Single(query.GetProperty("filter")
                .GetProperty("logical_operators").GetProperty("AND").EnumerateArray())
            .GetProperty("range").GetProperty("DATE").GetProperty("values")[0].GetProperty("gte").GetString()).ToArray();
        Assert.Equal(new[] { "2026-10-26T10:00:00.000Z", "2026-10-20T10:00:00.000Z" }, cutoffs);
        Assert.Empty(ready.Queries[2].GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray());
        using var itemsResponse = await fixture.Client.GetAsync($"/auto-runs/{ready.Id}/items");
        var item = Assert.Single((await Read<AutoRunItemsPage>(itemsResponse)).Items);
        Assert.Equal(new[] { "no-limit", "same-seven-days", "seven-days" }, item.Targets.Select(target => target.RuleId).Order());

        using var started = await fixture.Client.PostAsync($"/auto-runs/{ready.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        await WaitForState(fixture.Client, ready.Id, AutoRunState.Completed);
        AssertOrdinaryGatewayInput(Assert.Single(fixture.Broker.Messages).Message, ready.Id);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public async Task InvalidPhotoAgeFailsBeforeProviderWork(int days)
    {
        using var fixture = new Fixture();
        var rule = Rule("Invalid age", "tenant");
        rule.Match = new() { MaxPhotoAgeDays = days };
        fixture.Store.Add(RulesIndex, "shared-rule", rule);

        using var response = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", Request(), AutoRunJson.Options);

        await AssertStatus(response, HttpStatusCode.BadRequest);
        Assert.Empty(fixture.Provider.Calls);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Theory]
    [InlineData(false, 100000, 100)]
    [InlineData(true, 7, 7)]
    public async Task FiltersWithoutRulesSearchPagesAndPublishFrozenOrdinaryInput(
        bool includeEmptyTargets, int configuredMaximum, int expectedMaximum)
    {
        using var fixture = new Fixture(maxSelectedOverlays: configuredMaximum);
        // The empty rule store must not prevent a filter-only preview or require rule resolution.
        var request = new Dictionary<string, object>
        {
            ["source"] = new
            {
                mode = "Filters",
                filters = new { sensorNames = new[] { "camera" }, isRealtime = false, resolution = new { min = 0.5, max = 0.5 } }
            },
            ["publication"] = new { batchSize = 1, delayMs = 0 }
        };
        if (includeEmptyTargets) request["targets"] = Array.Empty<object>();
        using var optionsResponse = await fixture.Client.GetAsync("/auto-runs/options");
        await AssertStatus(optionsResponse, HttpStatusCode.OK);
        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        Assert.Equal(expectedMaximum, options.RootElement.GetProperty("defaults").GetProperty("filterMaxOverlays").GetInt32());

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var created = await Read<AutoRunSummary>(accepted);
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);
        Assert.Equal(AutoRunSourceMode.Filters, ready.SourceMode);
        Assert.Empty(ready.Targets);
        Assert.Single(ready.Queries);
        Assert.Equal(expectedMaximum, ready.MaxOverlays);
        Assert.Equal(1, ready.Counters.Scanned);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(0, ready.Counters.Invalid);
        Assert.True(ready.SourceExhausted);
        Assert.Empty(fixture.Broker.Messages);
        var calls = fixture.Provider.Calls.Where(call => call.Method == HttpMethod.Post).ToArray();
        Assert.Equal(2, calls.Length);
        Assert.Contains("result_type=4", Assert.Single(fixture.Provider.Calls, call => call.Method == HttpMethod.Get).Uri.Query);
        Assert.All(calls, call => Assert.Equal(HttpMethod.Post, call.Method));
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal("?searchAfter=next&to=1000", calls[1].Uri.Query);
        using (var query = JsonDocument.Parse(calls[0].Body!))
        {
            var clauses = query.RootElement.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND")
                .EnumerateArray().ToArray();
            var sensor = clauses.Single(c => c.TryGetProperty("match", out var match) && match.TryGetProperty("SENSOR", out _))
                .GetProperty("match").GetProperty("SENSOR");
            Assert.Equal("camera", sensor.GetProperty("values")[0].GetString());
        }
        using var itemsResponse = await fixture.Client.GetAsync($"/auto-runs/{created.Id}/items");
        var item = Assert.Single((await Read<AutoRunItemsPage>(itemsResponse)).Items);
        Assert.Empty(item.Targets);
        Assert.Equal(AutoRunItemOutcome.Pending, item.Outcome);

        fixture.Provider.ImageUrl = "https://images.invalid/changed-after-filter-preview";
        using var started = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        var completed = await WaitForState(fixture.Client, created.Id, AutoRunState.Completed);
        Assert.Equal(1, completed.Counters.Confirmed);
        Assert.Equal(0, completed.Counters.Remaining);
        var published = Assert.Single(fixture.Broker.Messages);
        var input = GatewayInputMessageParser.Parse(published.Message.Body);
        Assert.Equal("overlay-1", input.ImageId);
        Assert.Equal("https://images.invalid/original", input.ImageUrl);
        Assert.Equal(created.Id, published.Message.CorrelationId);
        using var body = JsonDocument.Parse(published.Message.Body);
        foreach (var name in new[] { "kind", "version", "runId", "overlay", "targets" })
            Assert.False(body.RootElement.TryGetProperty(name, out _));
        Assert.Contains("gridType", Assert.IsAssignableFrom<IEnumerable<object>>(published.Message.Headers!["x-updated-fields"]));
        Assert.Equal(3, fixture.Provider.Calls.Count);
    }

    [Fact]
    public async Task RerunCreatesFreshPreviewFromOriginalCriteriaAndCurrentRulesWithoutStartingIt()
    {
        using var fixture = new Fixture();
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Original rule", "original-tenant"));
        var request = Request() with
        {
            Source = new() { Mode = AutoRunSourceMode.Rules, Filters = new() { IsRealtime = false } },
            MinOverlays = 1,
            MaxOverlays = 1,
            Publication = new() { BatchSize = 1, DelayMs = 0 }
        };
        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var original = await Read<AutoRunSummary>(accepted);
        await WaitForState(fixture.Client, original.Id, AutoRunState.Ready);
        using var changed = await fixture.Client.PatchAsJsonAsync($"/auto-runs/{original.Id}/publication",
            new { batchSize = 2, delayMs = 40 });
        await AssertStatus(changed, HttpStatusCode.OK);
        using var started = await fixture.Client.PostAsync($"/auto-runs/{original.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        var originalCompleted = await WaitForState(fixture.Client, original.Id, AutoRunState.Completed);
        var originalMessage = Assert.Single(fixture.Broker.Messages).Message;

        var currentRule = Rule("Updated rule", "updated-tenant");
        currentRule.Match!.Resolution = new() { Min = 0.4, Max = 0.6 };
        fixture.Store.Add(RulesIndex, "shared-rule", currentRule);
        fixture.Provider.ImageUrl = "https://images.invalid/rerun";
        using var rerunResponse = await fixture.Client.PostAsync($"/auto-runs/{original.Id}/rerun", null);
        await AssertStatus(rerunResponse, HttpStatusCode.Accepted);
        var rerun = await Read<AutoRunSummary>(rerunResponse);
        Assert.NotEqual(original.Id, rerun.Id);
        Assert.EndsWith($"/auto-runs/{rerun.Id}", rerunResponse.Headers.Location!.ToString());
        var ready = await WaitForState(fixture.Client, rerun.Id, AutoRunState.Ready);
        Assert.Null(ready.StartedAt);
        Assert.Equal(AutoRunSourceMode.Rules, ready.SourceMode);
        Assert.Equal(1, ready.MinOverlays);
        Assert.Equal(1, ready.MaxOverlays);
        Assert.Equal(new AutoRunPublication { BatchSize = 2, DelayMs = 40 }, ready.Publication);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(0, ready.Counters.Confirmed);
        var target = Assert.Single(ready.Targets);
        Assert.Equal("FindAir", target.PipelineId);
        Assert.Equal("shared-rule", target.RuleId);
        Assert.Equal("Updated rule", target.RuleName);
        Assert.Equal(1, target.MinOverlays);
        Assert.Equal(1, target.MaxOverlays);
        Assert.Equal(0.4, target.Rule.Match!.Resolution!.Min);
        Assert.Equal("updated-tenant", target.Rule.RunParams![0].GetProperty("tenantId").GetString());
        var query = Assert.Single(ready.Queries);
        var clauses = query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND")
            .EnumerateArray().ToArray();
        Assert.False(clauses.Single(c => c.TryGetProperty("match", out var match) && match.TryGetProperty("ISREALTIME", out _))
            .GetProperty("match").GetProperty("ISREALTIME").GetProperty("values")[0].GetBoolean());
        var range = clauses.Single(c => c.TryGetProperty("range", out _)).GetProperty("range")
            .GetProperty("RESOLUTION").GetProperty("values")[0];
        Assert.Equal(0.4, range.GetProperty("gte").GetDouble());
        Assert.Equal(0.6, range.GetProperty("lte").GetDouble());
        Assert.Equal(6, fixture.Provider.Calls.Count);
        Assert.Single(fixture.Broker.Messages);
        using var originalResponse = await fixture.Client.GetAsync($"/auto-runs/{original.Id}");
        var retainedOriginal = await Read<AutoRunSummary>(originalResponse);
        // Memory is sampled per response; frozen run data must remain unchanged as the rerun progresses.
        Assert.Equal(JsonSerializer.Serialize(originalCompleted with { Memory = null }, AutoRunJson.Options),
            JsonSerializer.Serialize(retainedOriginal with { Memory = null }, AutoRunJson.Options));

        using var startRerun = await fixture.Client.PostAsync($"/auto-runs/{rerun.Id}/start", null);
        await AssertStatus(startRerun, HttpStatusCode.OK);
        await WaitForState(fixture.Client, rerun.Id, AutoRunState.Completed);
        var messages = fixture.Broker.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.NotEqual(originalMessage.MessageId, messages[1].Message.MessageId);
        AssertOrdinaryGatewayInput(messages[1].Message, rerun.Id);
        using var body = JsonDocument.Parse(messages[1].Message.Body);
        Assert.Equal("https://images.invalid/rerun", body.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal("Updated rule", Assert.Single(ready.Targets).Rule.RuleName);
    }

    [Fact]
    public async Task OmittedPublicationUsesConfiguredDefaultsAndReadySettingsCanBeChanged()
    {
        using var fixture = new Fixture();
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Inactive historical rule", "tenant"));
        var request = Request();
        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview",
            new { source = request.Source, targets = request.Targets }, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var created = await Read<AutoRunSummary>(accepted);
        Assert.Equal(new AutoRunPublication { BatchSize = 3, DelayMs = 17 }, created.Publication);
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);
        Assert.Equal(created.Publication, ready.ActivePublication);
        Assert.Equal(2, fixture.Provider.Calls.Count);
        Assert.All(fixture.Provider.Calls, call => Assert.Equal(HttpMethod.Get, call.Method));

        using var changed = await fixture.Client.PatchAsJsonAsync($"/auto-runs/{created.Id}/publication",
            new { batchSize = 2, delayMs = 40 });
        await AssertStatus(changed, HttpStatusCode.OK);
        var updated = await Read<AutoRunSummary>(changed);
        Assert.Equal(new AutoRunPublication { BatchSize = 2, DelayMs = 40 }, updated.Publication);
        Assert.Equal(updated.Publication, updated.ActivePublication);
        Assert.False(updated.PublicationChangePending);
        using var cancelled = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/cancel", null);
        await AssertStatus(cancelled, HttpStatusCode.OK);
        Assert.Equal(AutoRunState.Cancelled, (await Read<AutoRunSummary>(cancelled)).State);
        using var startCancelled = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(startCancelled, HttpStatusCode.Conflict);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"source":{"mode":"Rules"},"targets":[]}""")]
    [InlineData("""{"source":{"mode":"Filters"},"targets":[],"maxOverlays":0}""")]
    [InlineData("""{"source":{"mode":"OverlayIds","overlayIds":["overlay-1"]},"targets":null}""")]
    [InlineData("""{"source":{"mode":"OverlayIds","overlayIds":["overlay-1"],"filters":{}},"targets":[]}""")]
    [InlineData("""{"source":{"mode":"OverlayIds","overlayIds":[]},"targets":[{"pipelineId":"FindAir","ruleId":"shared-rule","maxOverlays":1}]}""")]
    [InlineData("""{"source":{"mode":"Rules"},"targets":[{"pipelineId":"FindAir","ruleId":"shared-rule"}]}""")]
    [InlineData("""{"source":{"mode":"Rules"},"targets":[{"pipelineId":"FindAir","ruleId":"shared-rule","maxOverlays":0}]}""")]
    [InlineData("""{"source":{"mode":"Rules"},"targets":[{"pipelineId":"FindAir","ruleId":"shared-rule","maxOverlays":1}],"publication":{"batchSize":101,"delayMs":0}}""")]
    [InlineData("""{"source":{"mode":"Filters","filters":{"photoTime":{"from":"2026-10-01T12:00:00"}}},"targets":[{"pipelineId":"FindAir","ruleId":"shared-rule","maxOverlays":1}]}""")]
    public async Task InvalidPreviewIsRejectedAsJsonWithoutProviderOrBrokerCalls(string json)
    {
        using var fixture = new Fixture();
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Rule", "tenant"));
        using var response = await fixture.Client.PostAsync("/auto-runs/preview",
            new StringContent(json, Encoding.UTF8, "application/json"));
        await AssertStatus(response, HttpStatusCode.BadRequest);
        Assert.Contains("json", response.Content.Headers.ContentType!.MediaType!);
        using var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Object, error.RootElement.ValueKind);
        Assert.Empty(fixture.Provider.Calls);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Theory]
    [InlineData("GET", "/auto-runs/missing", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auto-runs/missing/items", HttpStatusCode.NotFound)]
    [InlineData("POST", "/auto-runs/missing/start", HttpStatusCode.NotFound)]
    [InlineData("POST", "/auto-runs/missing/rerun", HttpStatusCode.NotFound)]
    [InlineData("POST", "/auto-runs/missing/cancel", HttpStatusCode.NotFound)]
    [InlineData("DELETE", "/auto-runs/missing", HttpStatusCode.NotFound)]
    [InlineData("GET", "/auto-runs/missing/items?from=-1", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/auto-runs/missing/items?size=201", HttpStatusCode.BadRequest)]
    [InlineData("GET", "/auto-runs/missing/items?outcome=wrong", HttpStatusCode.BadRequest)]
    public async Task MissingJobsAndInvalidItemQueriesReturnApiErrors(string method, string path, HttpStatusCode expected)
    {
        using var fixture = new Fixture();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        using var response = await fixture.Client.SendAsync(request);
        await AssertStatus(response, expected);
        Assert.Contains("json", response.Content.Headers.ContentType!.MediaType!);
        Assert.DoesNotContain("<!doctype", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UiRedirectDeepLinksAndAssetsAreServedWithoutSwallowingApiOrAsset404s()
    {
        using var fixture = new Fixture(enabled: false);
        using var redirect = await fixture.Client.GetAsync("/ui");
        Assert.Equal(HttpStatusCode.Redirect, redirect.StatusCode);
        Assert.Equal("/ui/auto-run", redirect.Headers.Location!.ToString());
        foreach (var route in new[] { "/ui/auto-run", "/ui/auto-run/runs/example-run" })
        {
            using var response = await fixture.Client.GetAsync(route);
            await AssertStatus(response, HttpStatusCode.OK);
            Assert.Equal("text/html", response.Content.Headers.ContentType!.MediaType);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("Flarules", html);
            Assert.Contains("/ui/main.js", html);
        }
        foreach (var asset in new[] { "main.js", "router.js", "api.js", "pages/auto-run.js", "styles.css", "workspace.css" })
        {
            using var response = await fixture.Client.GetAsync("/ui/" + asset);
            await AssertStatus(response, HttpStatusCode.OK);
            Assert.NotEqual("text/html", response.Content.Headers.ContentType!.MediaType);
            Assert.NotEmpty(await response.Content.ReadAsStringAsync());
        }
        foreach (var route in new[] { "/missing-api-route", "/ui/missing.js", "/ui/pages/missing.js" })
        {
            using var response = await fixture.Client.GetAsync(route);
            await AssertStatus(response, HttpStatusCode.NotFound);
            Assert.DoesNotContain("<!doctype", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task RunSizesAndSingleAndBulkDeletionAreAvailableAsJson()
    {
        using var fixture = new Fixture();
        var request = Request() with { Targets = [] };
        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        var created = await Read<AutoRunSummary>(accepted);
        var ready = await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);
        Assert.True(ready.RetainedBytes > 0);
        Assert.False(ready.DeletionRequested);
        using var history = await fixture.Client.GetAsync("/auto-runs");
        Assert.Equal(ready.RetainedBytes, Assert.Single(await Read<List<AutoRunSummary>>(history)).RetainedBytes);

        using var response = await fixture.Client.DeleteAsync($"/auto-runs/{ready.Id}");
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        var deletion = await Read<AutoRunDeletionResult>(response);
        Assert.Contains(ready.Id, deletion.DeletedIds.Concat(deletion.PendingIds));
        if (response.StatusCode == HttpStatusCode.OK) Assert.Equal(ready.RetainedBytes, deletion.ReleasedRetainedBytes);
        await WaitUntilDeleted(fixture.Client, ready.Id);
        using var items = await fixture.Client.GetAsync($"/auto-runs/{ready.Id}/items");
        await AssertStatus(items, HttpStatusCode.NotFound);

        using var next = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        var second = await Read<AutoRunSummary>(next);
        await WaitForState(fixture.Client, second.Id, AutoRunState.Ready);
        using var bulk = await fixture.Client.DeleteAsync("/auto-runs");
        Assert.Contains(bulk.StatusCode, new[] { HttpStatusCode.OK, HttpStatusCode.Accepted });
        var bulkResult = await Read<AutoRunDeletionResult>(bulk);
        Assert.Contains(second.Id, bulkResult.DeletedIds.Concat(bulkResult.PendingIds));
        await WaitUntilDeleted(fixture.Client, second.Id);
        using var empty = await fixture.Client.DeleteAsync("/auto-runs");
        await AssertStatus(empty, HttpStatusCode.OK);
        var noOp = await Read<AutoRunDeletionResult>(empty);
        Assert.Empty(noOp.DeletedIds);
        Assert.Empty(noOp.PendingIds);
        Assert.Equal(0, noOp.ReleasedRetainedBytes);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Fact]
    public async Task DeletingActiveRunReturnsAcceptedUntilProviderWorkFinishes()
    {
        using var fixture = new Fixture();
        var requested = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Provider.BeforeResponse = _ => { requested.TrySetResult(); return release.Task; };
        try
        {
            using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", Request() with { Targets = [] }, AutoRunJson.Options);
            var created = await Read<AutoRunSummary>(accepted);
            await requested.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var response = await fixture.Client.DeleteAsync($"/auto-runs/{created.Id}");
            await AssertStatus(response, HttpStatusCode.Accepted);
            var result = await Read<AutoRunDeletionResult>(response);
            Assert.Equal([created.Id], result.PendingIds);
            Assert.Empty(result.DeletedIds);
            Assert.Equal(0, result.ReleasedRetainedBytes);
            using var status = await fixture.Client.GetAsync($"/auto-runs/{created.Id}");
            Assert.True((await Read<AutoRunSummary>(status)).DeletionRequested);
            using var start = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
            await AssertStatus(start, HttpStatusCode.Conflict);
            release.TrySetResult();
            await WaitUntilDeleted(fixture.Client, created.Id);
            Assert.Empty(fixture.Broker.Messages);
        }
        finally { release.TrySetResult(); }
    }

    private static async Task WaitUntilDeleted(HttpClient client, string id)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5))
        {
            using var response = await client.GetAsync($"/auto-runs/{id}");
            if (response.StatusCode == HttpStatusCode.NotFound) return;
            await Task.Delay(10);
        }
        Assert.Fail("Run was not deleted after in-flight work ended.");
    }

    [Fact]
    public async Task DisabledFeatureStillExposesOptionsAndUiButRejectsPreview()
    {
        using var fixture = new Fixture(enabled: false);
        using var optionsResponse = await fixture.Client.GetAsync("/auto-runs/options");
        await AssertStatus(optionsResponse, HttpStatusCode.OK);
        using var options = JsonDocument.Parse(await optionsResponse.Content.ReadAsStringAsync());
        Assert.False(options.RootElement.GetProperty("enabled").GetBoolean());
        Assert.True(options.RootElement.GetProperty("singleInstance").GetBoolean());
        Assert.Equal(3, options.RootElement.GetProperty("defaults").GetProperty("publication").GetProperty("batchSize").GetInt32());
        Assert.Equal(100, options.RootElement.GetProperty("defaults").GetProperty("filterMaxOverlays").GetInt32());
        Assert.False(options.RootElement.TryGetProperty("providerTimeZone", out _));
        using var ui = await fixture.Client.GetAsync("/ui/auto-run");
        await AssertStatus(ui, HttpStatusCode.OK);
        using var response = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", Request(), AutoRunJson.Options);
        await AssertStatus(response, HttpStatusCode.ServiceUnavailable);
        Assert.Contains("disabled", await response.Content.ReadAsStringAsync());
        Assert.Empty(fixture.Provider.Calls);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Fact]
    public async Task OptionsExposeConfiguredMapProviderLimitsAndMemorySnapshotWithoutProviderSecrets()
    {
        const string mapUrl = "https://maps.example/viewer?mode=selection";
        const string secret = "options-provider-secret-canary";
        const int responseLimit = 96 * 1024 * 1024;
        const long memoryLimit = 1024L * 1024 * 1024;
        using var fixture = new Fixture(providerHeaders: new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer " + secret,
            ["X-Private-Provider-Key"] = secret
        }, configurationOverrides: new Dictionary<string, string?>
        {
            ["AutoRun:Ui:MapUrl"] = mapUrl,
            ["AutoRun:Provider:MaxResponseBytes"] = responseLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["AutoRun:Provider:TimeoutSeconds"] = "120",
            ["AutoRun:Provider:To"] = "2500",
            ["AutoRun:Memory:LimitBytes"] = memoryLimit.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["AutoRun:Memory:MaxUsagePercent"] = "75",
            ["AutoRun:Memory:MinimumFreeBytes"] = "33554432"
        });

        using var response = await fixture.Client.GetAsync("/auto-runs/options");
        await AssertStatus(response, HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(json);
        var options = document.RootElement;
        Assert.Equal(mapUrl, options.GetProperty("ui").GetProperty("mapUrl").GetString());
        Assert.Equal(responseLimit, options.GetProperty("provider").GetProperty("maxResponseBytes").GetInt32());
        Assert.Equal(120, options.GetProperty("provider").GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(2500, options.GetProperty("provider").GetProperty("to").GetInt32());
        var memory = options.GetProperty("memory");
        Assert.InRange(memory.GetProperty("limitBytes").GetInt64(), 0, memoryLimit);
        Assert.True(memory.GetProperty("availableBytes").GetInt64() >= 0);
        Assert.True(memory.GetProperty("processBytes").GetInt64() >= -1);
        Assert.True(memory.GetProperty("managedBytes").GetInt64() >= -1);
        Assert.True(memory.GetProperty("headroomBytes").GetInt64() >= 0);
        Assert.Contains(memory.GetProperty("isUnderPressure").ValueKind, new[] { JsonValueKind.True, JsonValueKind.False });
        Assert.DoesNotContain("Authorization", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("X-Private-Provider-Key", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        Assert.False(options.GetProperty("provider").TryGetProperty("headers", out _));
        Assert.Empty(fixture.Provider.Calls);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Fact]
    public async Task OptionsExposeDefaultResponseLimitAndUnconfiguredMap()
    {
        using var fixture = new Fixture(enabled: false);
        using var response = await fixture.Client.GetAsync("/auto-runs/options");
        await AssertStatus(response, HttpStatusCode.OK);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(64 * 1024 * 1024, document.RootElement.GetProperty("provider").GetProperty("maxResponseBytes").GetInt32());
        Assert.Equal(30, document.RootElement.GetProperty("provider").GetProperty("timeoutSeconds").GetInt32());
        Assert.Equal(1000, document.RootElement.GetProperty("provider").GetProperty("to").GetInt32());
        var ui = document.RootElement.GetProperty("ui");
        Assert.True(!ui.TryGetProperty("mapUrl", out var mapUrl) ||
            mapUrl.ValueKind == JsonValueKind.Null || string.IsNullOrWhiteSpace(mapUrl.GetString()));
    }

    [Fact]
    public void ConfigurationDefaultsPreserveAutomaticMemoryLimitAndHeadroom()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().Build();
        using var services = ConfigurationServices(configuration);
        services.GetRequiredService<IStartupValidator>().Validate();
        var options = services.GetRequiredService<IOptions<AutoRunOptions>>().Value;
        Assert.Equal(0, options.Memory.LimitBytes);
        Assert.Equal(80, options.Memory.MaxUsagePercent);
        Assert.Equal(256L * 1024 * 1024, options.Memory.MinimumFreeBytes);
        Assert.Equal(64 * 1024 * 1024, services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.MaxResponseBytes);
    }

    [Theory]
    [InlineData("AutoRun:Ui:MapUrl", "javascript:alert(1)")]
    [InlineData("AutoRun:Ui:MapUrl", "/relative-map")]
    [InlineData("AutoRun:Ui:MapUrl", "https://user:password@maps.example")]
    [InlineData("AutoRun:Ui:MapUrl", "file:///map.html")]
    [InlineData("AutoRun:Memory:LimitBytes", "-1")]
    [InlineData("AutoRun:Memory:MaxUsagePercent", "0")]
    [InlineData("AutoRun:Memory:MaxUsagePercent", "100")]
    [InlineData("AutoRun:Memory:MinimumFreeBytes", "-1")]
    [InlineData("AutoRun:Provider:MaxResponseBytes", "0")]
    [InlineData("AutoRun:Provider:MaxResponseBytes", "-1")]
    [InlineData("AutoRun:Provider:MaxResponseBytes", "536870913")]
    public void InvalidMapOrMemoryConfigurationFailsStartup(string name, string value)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [name] = value }).Build();
        using var services = ConfigurationServices(configuration);

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain("password", error.Message, StringComparison.Ordinal);
        Assert.NotEmpty(error.Failures);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("https://maps.example/viewer")]
    [InlineData("http://maps.example:8080/viewer")]
    public void OptionalOrAbsoluteHttpMapAndMaximumResponseLimitPassStartup(string? mapUrl)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AutoRun:Ui:MapUrl"] = mapUrl,
            ["AutoRun:Provider:MaxResponseBytes"] = "536870912"
        }).Build();
        using var services = ConfigurationServices(configuration);

        services.GetRequiredService<IStartupValidator>().Validate();

        Assert.Equal(mapUrl, services.GetRequiredService<IOptions<AutoRunOptions>>().Value.Ui.MapUrl);
        Assert.Equal(536870912, services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.MaxResponseBytes);
    }

    private static ServiceProvider ConfigurationServices(IConfiguration configuration)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData(AutoRunSourceMode.OverlayIds, false)]
    [InlineData(AutoRunSourceMode.OverlayIds, true)]
    [InlineData(AutoRunSourceMode.Rules, false)]
    [InlineData(AutoRunSourceMode.Filters, true)]
    public async Task ProviderHeadersAreSentWithoutExposingThemInApiResponsesOrGatewayMessages(AutoRunSourceMode mode, bool direct)
    {
        var headers = new Dictionary<string, string>
        {
            ["Authorization"] = "Bearer provider-test-only-token",
            ["X-Private-Provider-Key"] = "provider-test-only-key"
        };
        using var fixture = new Fixture(providerHeaders: headers);
        fixture.Store.Add(RulesIndex, "shared-rule", Rule("Historical rule", "tenant"));
        var request = Request() with
        {
            Source = new() { Mode = mode, OverlayIds = mode == AutoRunSourceMode.OverlayIds ? ["overlay-1"] : null },
            Targets = direct ? [] : Request().Targets
        };
        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        AssertNoHeaders(await accepted.Content.ReadAsStringAsync());
        var created = await Read<AutoRunSummary>(accepted);
        await WaitForState(fixture.Client, created.Id, AutoRunState.Ready);

        Assert.NotEmpty(fixture.Provider.Calls);
        Assert.All(fixture.Provider.Calls, call =>
        {
            foreach (var (name, value) in headers)
                Assert.Equal(value, Assert.Single(call.Headers[name]));
        });
        foreach (var route in new[] { "/auto-runs/options", "/auto-runs", $"/auto-runs/{created.Id}", $"/auto-runs/{created.Id}/items" })
        {
            using var response = await fixture.Client.GetAsync(route);
            await AssertStatus(response, HttpStatusCode.OK);
            AssertNoHeaders(await response.Content.ReadAsStringAsync());
        }

        using var started = await fixture.Client.PostAsync($"/auto-runs/{created.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        await WaitForState(fixture.Client, created.Id, AutoRunState.Completed);
        var message = Assert.Single(fixture.Broker.Messages).Message;
        AssertNoHeaders(Encoding.UTF8.GetString(message.Body));
        AssertNoHeaders(JsonSerializer.Serialize(message.Headers));

        void AssertNoHeaders(string json)
        {
            foreach (var (name, value) in headers)
            {
                Assert.DoesNotContain(name, json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(value, json, StringComparison.Ordinal);
            }
        }
    }

    [Theory]
    [InlineData(AutoRunSourceMode.Rules)]
    [InlineData(AutoRunSourceMode.Filters)]
    [InlineData(AutoRunSourceMode.OverlayIds)]
    public async Task ExactRawSensorTypeReachesPreviewAndOrdinaryGatewayInput(AutoRunSourceMode mode)
    {
        using var fixture = new Fixture();
        fixture.Provider.RawSensorType = "VIS";
        var rule = Rule("Visible images", "tenant");
        rule.Match!.SensorTypes = ["VIS"];
        fixture.Store.Add(RulesIndex, "shared-rule", rule);
        var request = Request() with
        {
            Source = new()
            {
                Mode = mode,
                OverlayIds = mode == AutoRunSourceMode.OverlayIds ? ["overlay-1"] : null,
                Filters = mode == AutoRunSourceMode.Filters ? new() { SensorTypes = ["VIS"] } : null
            }
        };

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var admitted = await Read<AutoRunSummary>(accepted);
        var ready = await WaitForState(fixture.Client, admitted.Id, AutoRunState.Ready);
        Assert.Equal(1, ready.Counters.Selected);
        Assert.Equal(new[] { "VIS" }, Assert.Single(ready.Targets).Rule.Match!.SensorTypes);
        Assert.Single(fixture.Provider.Calls, call => call.Uri.Query.Contains("result_type=4", StringComparison.Ordinal));
        if (mode != AutoRunSourceMode.OverlayIds)
        {
            var query = Assert.Single(ready.Queries);
            Assert.Contains("sensorType.keyword", query.GetRawText(), StringComparison.Ordinal);
            Assert.Contains("VIS", query.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("IMAGINGTECHNIQUE", query.GetRawText(), StringComparison.Ordinal);
        }
        using var itemsResponse = await fixture.Client.GetAsync($"/auto-runs/{ready.Id}/items");
        Assert.Equal("VIS", Assert.Single((await Read<AutoRunItemsPage>(itemsResponse)).Items).SensorType);

        using var started = await fixture.Client.PostAsync($"/auto-runs/{ready.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.OK);
        await WaitForState(fixture.Client, ready.Id, AutoRunState.Completed);
        var message = Assert.Single(fixture.Broker.Messages).Message;
        AssertOrdinaryGatewayInput(message, ready.Id);
        using var body = JsonDocument.Parse(message.Body);
        Assert.Equal("VIS", body.RootElement.GetProperty("sensorType").GetString());
    }

    [Fact]
    public async Task CoarseEOResponseCannotMakeActualPANMatchVisibleRule()
    {
        using var fixture = new Fixture();
        fixture.Provider.RawSensorType = "PAN";
        var rule = Rule("Visible images", "tenant");
        rule.Match!.SensorTypes = ["VIS"];
        fixture.Store.Add(RulesIndex, "shared-rule", rule);

        using var accepted = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", Request(), AutoRunJson.Options);
        await AssertStatus(accepted, HttpStatusCode.Accepted);
        var ready = await WaitForState(fixture.Client, (await Read<AutoRunSummary>(accepted)).Id, AutoRunState.Ready);

        Assert.Equal(0, ready.Counters.Selected);
        Assert.Equal(1, Assert.Single(ready.Targets).Shortfall);
        using var started = await fixture.Client.PostAsync($"/auto-runs/{ready.Id}/start", null);
        await AssertStatus(started, HttpStatusCode.Conflict);
        Assert.Empty(fixture.Broker.Messages);
    }

    [Theory]
    [InlineData(false, "vis")]
    [InlineData(false, "unsupported")]
    [InlineData(true, "vis")]
    [InlineData(true, "unsupported")]
    public async Task UnknownSensorTypesReturnBadRequestBeforeAJobOrProviderCall(bool ruleType, string type)
    {
        using var fixture = new Fixture();
        var rule = Rule("Typed rule", "tenant");
        if (ruleType) rule.Match!.SensorTypes = [type];
        fixture.Store.Add(RulesIndex, "shared-rule", rule);
        var request = Request() with
        {
            Source = new()
            {
                Mode = AutoRunSourceMode.Rules,
                Filters = ruleType ? null : new() { SensorTypes = [type] }
            }
        };

        using var rejected = await fixture.Client.PostAsJsonAsync("/auto-runs/preview", request, AutoRunJson.Options);

        await AssertStatus(rejected, HttpStatusCode.BadRequest);
        var body = await rejected.Content.ReadAsStringAsync();
        Assert.Contains(type, body, StringComparison.Ordinal);
        Assert.Contains(ruleType ? "shared-rule" : "source.filters.sensorTypes", body, StringComparison.Ordinal);
        Assert.Empty(fixture.Provider.Calls);
        using var jobs = await fixture.Client.GetAsync("/auto-runs");
        Assert.Empty(await Read<List<AutoRunSummary>>(jobs));
    }

    private static AutoRunPreviewRequest Request() => new()
    {
        Source = new() { Mode = AutoRunSourceMode.OverlayIds, OverlayIds = ["overlay-1"] },
        Targets = [new() { PipelineId = "FindAir", RuleId = "shared-rule", MinOverlays = 1, MaxOverlays = 1 }]
    };

    private static PipelineRuleDocument Rule(string name, string tenant) => new()
    {
        RuleName = name, IsActive = false,
        Match = new()
        {
            Sensors = [new() { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate], GridTypes = ["grid"] }]
        },
        RunParams = [JsonSerializer.SerializeToElement(new
        {
            tenantId = tenant, algorithmNames = new[] { "FindAir" },
            tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
        })]
    };

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(AutoRunJson.Options))!;

    private static void AssertOrdinaryGatewayInput(RabbitMqMessageEnvelope message, string runId)
    {
        Assert.Equal(runId, message.CorrelationId);
        using var body = JsonDocument.Parse(message.Body);
        Assert.Equal("overlay-1", body.RootElement.GetProperty("id").GetString());
        foreach (var name in new[] { "kind", "version", "runId", "overlay", "targets" })
            Assert.False(body.RootElement.TryGetProperty(name, out _));
        Assert.Equal(new object[] { "gridType" },
            Assert.IsAssignableFrom<IEnumerable<object>>(message.Headers!["x-updated-fields"]));
        Assert.Equal("overlay-1", GatewayInputMessageParser.Parse(message.Body).ImageId);
    }

    private static async Task AssertStatus(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(expected == response.StatusCode, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static async Task<AutoRunSummary> WaitForState(HttpClient client, string id, AutoRunState expected)
    {
        var elapsed = Stopwatch.StartNew();
        AutoRunSummary? latest = null;
        while (elapsed.Elapsed < TimeSpan.FromSeconds(10))
        {
            using var response = await client.GetAsync($"/auto-runs/{id}");
            await AssertStatus(response, HttpStatusCode.OK);
            latest = await Read<AutoRunSummary>(response);
            if (latest.State == expected) return latest;
            if (latest.State is AutoRunState.Failed or AutoRunState.Cancelled or AutoRunState.Expired) break;
            await Task.Delay(10);
        }
        Assert.Fail($"Expected {expected}; last state {latest?.State}. Errors: {string.Join("; ", latest?.Errors ?? [])}");
        return latest!;
    }

    private sealed class Fixture : IDisposable
    {
        private readonly RulesApiFactory _baseFactory;
        private readonly WebApplicationFactory<Program> _app;
        public InMemoryIndexStore Store { get; } = new();
        public ProviderHandler Provider { get; } = new();
        public CapturingBroker Broker { get; } = new();
        public HttpClient Client { get; }

        public Fixture(bool enabled = true, IReadOnlyDictionary<string, string>? providerHeaders = null, int maxSelectedOverlays = 100000,
            IReadOnlyDictionary<string, string?>? configurationOverrides = null, TimeProvider? time = null)
        {
            _baseFactory = new(Store);
            _app = _baseFactory.WithWebHostBuilder(builder =>
            {
                var configuration = new Dictionary<string, string?>
                {
                    ["AutoRun:Enabled"] = enabled.ToString(),
                    ["AutoRun:MaxSelectedOverlays"] = maxSelectedOverlays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["AutoRun:Provider:BaseUrl"] = enabled ? "http://provider.invalid" : "",
                    ["AutoRun:Provider:RequestDelayMs"] = "0",
                    ["AutoRun:Publication:BatchSize"] = "3", ["AutoRun:Publication:DelayMs"] = "17",
                    ["AutoRun:Destination:Connection:Name"] = "gateway-test",
                    ["AutoRun:Destination:Connection:Host"] = "broker.invalid",
                    ["AutoRun:Destination:Connection:Port"] = "5672",
                    ["AutoRun:Destination:Connection:Username"] = "test",
                    ["AutoRun:Destination:Connection:Password"] = "test",
                    ["AutoRun:Destination:Connection:VirtualHost"] = "/",
                    ["AutoRun:Destination:QueueName"] = "unified-gateway.input"
                };
                if (providerHeaders is not null)
                    foreach (var (name, value) in providerHeaders)
                        configuration[$"AutoRun:Provider:Headers:{name}"] = value;
                if (configurationOverrides is not null)
                    foreach (var (name, value) in configurationOverrides)
                        configuration[name] = value;
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
                builder.ConfigureServices(services =>
                {
                    if (time is not null)
                    {
                        services.RemoveAll<TimeProvider>();
                        services.AddSingleton(time);
                    }
                    services.AddHttpClient<IElasticOverlayProvider, ElasticOverlayProvider>()
                        .ConfigurePrimaryHttpMessageHandler(() => Provider);
                    services.RemoveAll<IRabbitMqDestinationPublisher>();
                    services.AddSingleton<IRabbitMqDestinationPublisher>(Broker);
                });
            });
            Client = _app.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        }

        public void Dispose()
        {
            Client.Dispose();
            _app.Dispose();
            _baseFactory.Dispose();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private long _utcTicks = now.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref _utcTicks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref _utcTicks, duration.Ticks);
    }

    private sealed record ProviderCall(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public string ImageUrl { get; set; } = "https://images.invalid/original";
        public string PhotoTime { get; set; } = "2000-01-02T10:00:00Z";
        public JsonElement ImageWidth { get; set; } = JsonSerializer.SerializeToElement(100);
        public JsonElement ImageHeight { get; set; } = JsonSerializer.SerializeToElement(50);
        public string RawSensorType { get; set; } = "EO";
        public ConcurrentQueue<ProviderCall> Calls { get; } = new();
        public Func<CancellationToken, Task>? BeforeResponse { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Enqueue(new(request.Method, request.RequestUri!, body,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            if (BeforeResponse is { } wait) await wait(cancellationToken);
            var entity = new
            {
                exclusive_id = new { entity_id = "overlay-1" }, geo = new { wkt = Area },
                properties_list = new
                {
                    GlobalId = "overlay-1", Url = ImageUrl, GridPath = "https://grids.invalid/original",
                    Sensor = "camera", ImagingTechnique = "EO", SolutionType = "grid", Sortie = "sortie-1",
                    RegistrationQuality = "Accurate", Resolution = 0.5, ImageWidth, ImageHeight,
                    PhotoTime, Created = "2000-01-02T11:00:00Z", IsRealTime = false
                }
            };
            object response = request.Method == HttpMethod.Get
                ? request.RequestUri!.Query.Contains("result_type=4", StringComparison.Ordinal)
                    ? new { id = "overlay-1", sensorType = RawSensorType } : entity
                :
                new
                {
                    entities_list = !request.RequestUri!.Query.Contains("searchAfter", StringComparison.Ordinal) ? new[] { entity } : [],
                    next_page = !request.RequestUri.Query.Contains("searchAfter", StringComparison.Ordinal)
                        ? "http://provider.invalid/ErisImages/v2.0/ErisImages/Images/TableEntities?searchAfter=next" : null
                };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(response), Encoding.UTF8, "application/json") };
        }
    }

    private sealed record PublishedMessage(RabbitMqDestination Destination, RabbitMqMessageEnvelope Message);

    private sealed class CapturingBroker : IRabbitMqDestinationPublisher
    {
        public ConcurrentQueue<PublishedMessage> Messages { get; } = new();
        public Task PublishAsync(RabbitMqDestination destination, RabbitMqMessageEnvelope message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Messages.Enqueue(new(destination, message with { Body = message.Body.ToArray() }));
            return Task.CompletedTask;
        }
    }
}
