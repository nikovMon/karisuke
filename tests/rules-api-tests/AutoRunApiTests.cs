using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Historical;
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

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunApiTests
{
    private const string RulesIndex = "asd-integ-pipeline-index";
    private const string OtherPipelineIndex = "algo-integ-pipeline-index";
    private const string Area = "POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0))";

    [Theory]
    [InlineData(AutoRunSourceMode.Rules)]
    [InlineData(AutoRunSourceMode.Filters)]
    public async Task PreviewThroughPublicationUsesRealAdaptersAndFrozenScopedHistoricalData(AutoRunSourceMode mode)
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
        Assert.Equal(1, target.Rule.Match!.MaxPhotoAgeDays);
        Assert.Empty(fixture.Broker.Messages);

        var providerCalls = fixture.Provider.Calls.ToArray();
        Assert.Equal(2, providerCalls.Length);
        Assert.All(providerCalls, call => Assert.Equal(HttpMethod.Post, call.Method));
        Assert.Equal(providerCalls[0].Body, providerCalls[1].Body);
        Assert.Equal("?searchAfter=next", providerCalls[1].Uri.Query);
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

        // Editing both upstream sources after Ready must not affect the frozen dispatch.
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
        var envelope = JsonSerializer.Deserialize<HistoricalOverlayEnvelope>(published.Message.Body, AutoRunJson.Options)!;
        Assert.Equal(HistoricalOverlayEnvelope.MessageKind, envelope.Kind);
        Assert.Equal(HistoricalOverlayEnvelope.CurrentVersion, envelope.Version);
        Assert.Equal(created.Id, envelope.RunId);
        var frozenTarget = Assert.Single(envelope.Targets);
        Assert.Equal("FindAir", frozenTarget.PipelineId);
        Assert.Equal("shared-rule", frozenTarget.Rule.Id);
        Assert.Equal("Original inactive rule", frozenTarget.Rule.RuleName);
        Assert.False(frozenTarget.Rule.IsActive);
        Assert.Equal(1, frozenTarget.Rule.Match!.MaxPhotoAgeDays);
        Assert.Equal("original-tenant", frozenTarget.Rule.RunParams![0].GetProperty("tenantId").GetString());
        var overlay = envelope.Overlay;
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
        Assert.Equal(2, fixture.Provider.Calls.Count);

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
        Assert.Equal(HttpMethod.Get, Assert.Single(fixture.Provider.Calls).Method);
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
        var calls = fixture.Provider.Calls.ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call => Assert.Equal(HttpMethod.Post, call.Method));
        Assert.Equal(calls[0].Body, calls[1].Body);
        Assert.Equal("?searchAfter=next", calls[1].Uri.Query);
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
        Assert.Equal(2, fixture.Provider.Calls.Count);
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
        Assert.Equal(4, fixture.Provider.Calls.Count);
        Assert.Single(fixture.Broker.Messages);
        using var originalResponse = await fixture.Client.GetAsync($"/auto-runs/{original.Id}");
        var retainedOriginal = await Read<AutoRunSummary>(originalResponse);
        Assert.Equal(JsonSerializer.Serialize(originalCompleted, AutoRunJson.Options),
            JsonSerializer.Serialize(retainedOriginal, AutoRunJson.Options));

        using var startRerun = await fixture.Client.PostAsync($"/auto-runs/{rerun.Id}/start", null);
        await AssertStatus(startRerun, HttpStatusCode.OK);
        await WaitForState(fixture.Client, rerun.Id, AutoRunState.Completed);
        var messages = fixture.Broker.Messages.ToArray();
        Assert.Equal(2, messages.Length);
        Assert.NotEqual(originalMessage.MessageId, messages[1].Message.MessageId);
        var envelope = JsonSerializer.Deserialize<HistoricalOverlayEnvelope>(messages[1].Message.Body, AutoRunJson.Options)!;
        Assert.Equal(rerun.Id, envelope.RunId);
        Assert.Equal("https://images.invalid/rerun", envelope.Overlay.GetProperty("imageUrl").GetString());
        Assert.Equal("Updated rule", Assert.Single(envelope.Targets).Rule.RuleName);
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
        Assert.Equal(HttpMethod.Get, Assert.Single(fixture.Provider.Calls).Method);

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
            Sensors = [new() { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate], GridTypes = ["grid"] }],
            MaxPhotoAgeDays = 1
        },
        RunParams = [JsonSerializer.SerializeToElement(new
        {
            tenantId = tenant, algorithmNames = new[] { "FindAir" },
            tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
        })]
    };

    private static async Task<T> Read<T>(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<T>(AutoRunJson.Options))!;

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

        public Fixture(bool enabled = true, IReadOnlyDictionary<string, string>? providerHeaders = null, int maxSelectedOverlays = 100000)
        {
            _baseFactory = new(Store);
            _app = _baseFactory.WithWebHostBuilder(builder =>
            {
                var configuration = new Dictionary<string, string?>
                {
                    ["AutoRun:Enabled"] = enabled.ToString(),
                    ["AutoRun:MaxSelectedOverlays"] = maxSelectedOverlays.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["AutoRun:Provider:BaseUrl"] = enabled ? "http://provider.invalid" : "",
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
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(configuration));
                builder.ConfigureServices(services =>
                {
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

    private sealed record ProviderCall(HttpMethod Method, Uri Uri, string? Body, IReadOnlyDictionary<string, string[]> Headers);

    private sealed class ProviderHandler : HttpMessageHandler
    {
        public string ImageUrl { get; set; } = "https://images.invalid/original";
        public ConcurrentQueue<ProviderCall> Calls { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Calls.Enqueue(new(request.Method, request.RequestUri!, body,
                request.Headers.ToDictionary(header => header.Key, header => header.Value.ToArray(), StringComparer.OrdinalIgnoreCase)));
            var entity = new
            {
                exclusive_id = new { entity_id = "overlay-1" }, geo = new { wkt = Area },
                properties_list = new
                {
                    GlobalId = "overlay-1", Url = ImageUrl, GridPath = "https://grids.invalid/original",
                    Sensor = "camera", ImagingTechnique = "EO", SolutionType = "grid", Sortie = "sortie-1",
                    RegistrationQuality = "Accurate", Resolution = 0.5, ImageWidth = 100, ImageHeight = 50,
                    PhotoTime = "2000-01-02T10:00:00Z", Created = "2000-01-02T11:00:00Z", IsRealTime = false
                }
            };
            object response = request.Method == HttpMethod.Get ? entity :
                new
                {
                    entities_list = request.RequestUri!.Query.Length == 0 ? new[] { entity } : [],
                    next_page = request.RequestUri.Query.Length == 0
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
