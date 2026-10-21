using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImagingPipeline.Common.Dtos.Historical;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Historical;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.UnifiedGateway.Configuration;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using ImagingPipeline.UnifiedGateway.Rules;
using ImagingPipeline.UnifiedGateway.Source;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class HistoricalSourceMessageHandlerTests
{
    [Fact]
    public async Task InactiveAgeOnlyRuleCanRunHistoricalOverlayWithoutUpdatedFields()
    {
        var rule = Rule("saved-rule");
        rule.IsActive = false;
        rule.Match = new RuleMatchConditions { MaxPhotoAgeDays = 1 };
        using var gateway = new Gateway();

        var result = await gateway.Handler.HandleAsync(Message(rule: rule));

        Assert.True(result.IsSuccess);
        Assert.Single(gateway.Transport.Sent);
        Assert.Equal(1, rule.Match.MaxPhotoAgeDays);
        Assert.False(rule.IsActive);
        var image = GatewayInputMessageParser.Parse(Encoding.UTF8.GetBytes(Overlay().ToJsonString()));
        var liveSnapshot = RuleSnapshotBuilder.Build(new RuleLoadResult([rule], []), new FindAirPipelineContract());
        Assert.Empty(new RuleMatcher(TimeProvider.System).Match(image, liveSnapshot.Rules).Matches);
        Assert.Single(new RuleMatcher(TimeProvider.System).Match(
            image, [HistoricalRuleSnapshot.Build(rule, new FindAirPipelineContract())]).Matches);
    }

    [Fact]
    public async Task FrozenTargetsIgnoreLiveRulesAndDoNotFanOutToOtherPipelines()
    {
        using var gateway = new Gateway();
        await gateway.Cache.StartAsync(CancellationToken.None);
        try
        {
            var frozen = Rule("saved-rule");
            frozen.IsActive = false;
            frozen.RunParams = [RunParams("frozen-tenant")];

            var result = await gateway.Handler.HandleAsync(Message(rule: frozen));

            Assert.True(result.IsSuccess);
            var sent = Assert.Single(gateway.Transport.Sent);
            Assert.Equal("pipeline-a", sent.PipelineId);
            using var payload = JsonDocument.Parse(sent.Work.Payload.Body);
            Assert.Equal("saved-rule", payload.RootElement.GetProperty("ruleId").GetString());
            Assert.Equal("frozen-tenant", payload.RootElement.GetProperty("tenantId").GetString());
            Assert.Equal(1, GeometryUtilities.ReadGeoJson(payload.RootElement.GetProperty("roiFootprint")).Area, 9);
        }
        finally
        {
            await gateway.Cache.StopAsync(CancellationToken.None);
        }
    }

    [Theory]
    [InlineData("sensorName", "another-camera")]
    [InlineData("registrationQuality", "Sensor")]
    [InlineData("gridType", "another-grid")]
    [InlineData("modeSAR", "another-mode")]
    public async Task HistoricalBypassPreservesNonAgeStringConditions(string field, string value)
    {
        using var gateway = new Gateway();
        var overlay = Overlay();
        overlay[field] = value;

        var result = await gateway.Handler.HandleAsync(Message(overlay: overlay, rule: ConstrainedRule()));

        AssertRejected(result, gateway, "gateway.historical_rule_mismatch");
    }

    [Theory]
    [InlineData("resolution")]
    [InlineData("geometry")]
    public async Task HistoricalBypassPreservesResolutionAndGeometry(string mismatch)
    {
        using var gateway = new Gateway();
        var rule = ConstrainedRule();
        if (mismatch == "resolution")
            rule.Match!.Resolution = new ResolutionRange { Min = 2 };
        else
            rule.Match!.LocationWkt = "POLYGON ((50 50, 51 50, 51 51, 50 51, 50 50))";

        var result = await gateway.Handler.HandleAsync(Message(rule: rule));

        AssertRejected(result, gateway, "gateway.historical_rule_mismatch");
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void SnapshotValidatesOriginalRuleBeforeRemovingAge(int age)
    {
        var rule = Rule("invalid");
        rule.Match = new RuleMatchConditions { MaxPhotoAgeDays = age };

        Assert.Throws<ArgumentException>(() => HistoricalRuleSnapshot.Build(rule, new FindAirPipelineContract()));
    }

    [Fact]
    public async Task HistoricalInputIsDisabledByDefaultAndCannotFallThroughToLiveMatching()
    {
        Assert.False(new HistoricalInputOptions().Enabled);
        using var gateway = new Gateway(enabled: false);

        var result = await gateway.Handler.HandleAsync(Message() with { Headers = GridUpdatedHeaders() });

        AssertRejected(result, gateway, "gateway.historical_disabled");
    }

    [Theory]
    [InlineData("kind")]
    [InlineData("version")]
    [InlineData("runId")]
    [InlineData("overlay")]
    [InlineData("targets")]
    public async Task MissingRequiredEnvelopePropertyIsRejected(string field)
    {
        using var gateway = new Gateway();
        var body = EnvelopeNode();
        body.Remove(field);

        var result = await gateway.Handler.HandleAsync(RawMessage(body.ToJsonString()));

        AssertRejected(result, gateway, "gateway.historical_invalid_envelope");
    }

    [Theory]
    [InlineData("kind", "\"unknown-kind\"")]
    [InlineData("version", "99")]
    [InlineData("runId", "\" \"")]
    [InlineData("runId", "null")]
    [InlineData("overlay", "[]")]
    [InlineData("targets", "[]")]
    [InlineData("targets", "null")]
    [InlineData("version", "\"1\"")]
    public async Task InvalidEnvelopeIsRejectedEvenWithLiveUpdateHeaders(string field, string rawValue)
    {
        using var gateway = new Gateway();
        var body = EnvelopeNode();
        body[field] = JsonNode.Parse(rawValue);

        var result = await gateway.Handler.HandleAsync(
            RawMessage(body.ToJsonString()) with { Headers = GridUpdatedHeaders() });

        AssertRejected(result, gateway, "gateway.historical_invalid_envelope");
    }

    [Theory]
    [InlineData("""{"kind":"historical-auto-run","version":1""")]
    [InlineData("""{"kind":"historical-auto-run","kind":"unknown"}""")]
    public async Task MalformedOrDuplicateEnvelopeIsRejected(string json)
    {
        using var gateway = new Gateway();

        var result = await gateway.Handler.HandleAsync(RawMessage(json));

        AssertRejected(result, gateway, "gateway.historical_invalid_envelope");
    }

    [Fact]
    public async Task DuplicateNestedPropertiesAreRejected()
    {
        using var gateway = new Gateway();
        var json = Encoding.UTF8.GetString(Message().Body)
            .Replace("\"id\":\"image-a\"", "\"id\":\"image-a\",\"id\":\"image-b\"", StringComparison.Ordinal);

        var result = await gateway.Handler.HandleAsync(RawMessage(json));

        AssertRejected(result, gateway, "gateway.historical_invalid_envelope");
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disabled")]
    public async Task UnknownOrDisabledPipelineRejectsAllTargetsBeforeDispatch(string pipelineId)
    {
        using var gateway = new Gateway();
        var targets = new List<HistoricalRuleTarget>
        {
            Target("pipeline-a", Rule("valid")),
            Target(pipelineId, Rule("other"))
        };

        var result = await gateway.Handler.HandleAsync(Message(targets: targets));

        AssertRejected(result, gateway, "gateway.historical_invalid_pipeline");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DuplicateOrConflictingTargetsAreRejected(bool conflicting)
    {
        using var gateway = new Gateway();
        var second = Rule("saved-rule");
        if (conflicting)
            second.RunParams = [RunParams("different-tenant")];

        var result = await gateway.Handler.HandleAsync(Message(targets:
            [Target("pipeline-a", Rule("saved-rule")), Target("pipeline-a", second)]));

        AssertRejected(result, gateway, "gateway.historical_duplicate_target");
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"pipelineId":"","rule":null}""")]
    [InlineData("""{"pipelineId":"pipeline-a","rule":null}""")]
    [InlineData("""{"pipelineId":"pipeline-a","rule":{"_id":""}}""")]
    public async Task MalformedTargetsAreRejected(string target)
    {
        using var gateway = new Gateway();
        var body = EnvelopeNode();
        body["targets"] = new JsonArray(JsonNode.Parse(target));

        var result = await gateway.Handler.HandleAsync(RawMessage(body.ToJsonString()));

        AssertRejected(result, gateway, "gateway.historical_invalid_target");
    }

    [Theory]
    [InlineData("rule")]
    [InlineData("match")]
    public async Task LaterInvalidTargetPreventsEarlierValidTargetFromDispatching(string failure)
    {
        using var gateway = new Gateway();
        var second = Rule("second");
        if (failure == "rule")
            second.RunParams = [JsonSerializer.SerializeToElement(new { invalid = true })];
        else
            second.Match!.ModeSar = ["different-mode"];

        var result = await gateway.Handler.HandleAsync(Message(targets:
            [Target("pipeline-a", Rule("first")), Target("pipeline-b", second)]));

        AssertRejected(result, gateway,
            failure == "rule" ? "gateway.historical_invalid_target" : "gateway.historical_rule_mismatch");
    }

    [Theory]
    [InlineData("imageUrl")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("sensorType")]
    [InlineData("gridURI")]
    public async Task ContractSpecificOverlayRequirementsAreValidatedBeforeDispatch(string field)
    {
        using var gateway = new Gateway();
        var overlay = Overlay();
        overlay.Remove(field);

        var result = await gateway.Handler.HandleAsync(Message(overlay: overlay));

        AssertRejected(result, gateway, "gateway.historical_invalid_context");
    }

    [Fact]
    public async Task RejectedOverlayAndTargetValidationRetainsRunAndImageCorrelation()
    {
        using var gateway = new Gateway();
        var overlay = Overlay();
        overlay.Remove("imageUrl");
        using var activity = new System.Diagnostics.Activity("test-historical-rejection").Start();

        var result = await gateway.Handler.HandleAsync(Message(overlay: overlay));

        AssertRejected(result, gateway, "gateway.historical_invalid_context");
        var rejection = Assert.Single(gateway.Logger.Entries, entry => entry.EventId == 6020);
        Assert.Equal("run-a", rejection.Fields["auto_run.run_id"]);
        Assert.Equal(HistoricalOverlayEnvelope.MessageKind, rejection.Fields["unified_gateway.input_kind"]);
        Assert.Equal("image-a", rejection.Fields[TelemetryAttributeNames.PipelineImageId]);
        Assert.Equal("run-a", activity.GetTagItem("auto_run.run_id"));
    }

    [Fact]
    public async Task WholeMessageRetryResendsSuccessfulSiblingsWithStableIdsAndNewRunUsesNewIds()
    {
        using var gateway = new Gateway(send: unit => unit.PipelineId == "pipeline-b"
            ? DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout)
            : DispatchOutcome.Delivered(unit));
        var targets = new List<HistoricalRuleTarget>
        {
            Target("pipeline-a", Rule("rule-a")), Target("pipeline-b", Rule("rule-b"))
        };
        var message = Message(targets: targets);

        var first = await gateway.Handler.HandleAsync(message);
        var retry = await gateway.Handler.HandleAsync(message);
        var newRun = await gateway.Handler.HandleAsync(Message(targets: targets, runId: "new-run"));

        Assert.Equal(RabbitMqMessageFailureAction.Retry, first.FailureAction);
        Assert.Equal(RabbitMqMessageFailureAction.Retry, retry.FailureAction);
        Assert.Equal(RabbitMqMessageFailureAction.Retry, newRun.FailureAction);
        foreach (var units in gateway.Transport.Sent.GroupBy(unit => unit.PipelineId))
        {
            var attempts = units.ToArray();
            Assert.Equal(3, attempts.Length);
            Assert.Equal(attempts[0].DispatchId, attempts[1].DispatchId);
            Assert.NotEqual(attempts[0].DispatchId, attempts[2].DispatchId);
        }
    }

    [Fact]
    public async Task DuplicateRunsCollapseButDifferentRunsAreDispatched()
    {
        using var gateway = new Gateway();
        var rule = Rule("saved-rule");
        rule.RunParams = [RunParams("one"), RunParams("two"), RunParams("one")];

        var result = await gateway.Handler.HandleAsync(Message(rule: rule));

        Assert.True(result.IsSuccess);
        Assert.Equal(2, gateway.Transport.Sent.Count);
    }

    [Fact]
    public async Task OrdinaryLiveMessagesKeepTheirGateRuleFanOutAndIdentity()
    {
        using var gateway = new Gateway();
        await gateway.Cache.StartAsync(CancellationToken.None);
        try
        {
            var body = Overlay();
            body["runId"] = "unrelated-live-metadata";
            var withoutHeader = RawMessage(body.ToJsonString());
            Assert.True((await gateway.Handler.HandleAsync(withoutHeader)).IsSuccess);
            Assert.Empty(gateway.Transport.Sent);

            var result = await gateway.Handler.HandleAsync(withoutHeader with { Headers = GridUpdatedHeaders() });

            Assert.True(result.IsSuccess);
            Assert.Equal(2, gateway.Transport.Sent.Count);
            foreach (var unit in gateway.Transport.Sent)
                Assert.Equal(DispatchIdentity.Create("image-a", unit.PipelineId, "live-rule", RunParams("live-tenant")), unit.DispatchId);
        }
        finally
        {
            await gateway.Cache.StopAsync(CancellationToken.None);
        }
    }

    private static void AssertRejected(RabbitMqMessageProcessingResult result, Gateway gateway, string code)
    {
        Assert.False(result.IsSuccess);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        Assert.Equal(code, result.Error);
        Assert.Empty(gateway.Transport.Sent);
    }

    private static PipelineRuleDocument ConstrainedRule()
    {
        var rule = Rule("constrained");
        rule.Match!.MaxPhotoAgeDays = 1;
        rule.Match.ModeSar = ["stripmap"];
        rule.Match.Resolution = new ResolutionRange { Min = 0.5, Max = 1 };
        rule.Match.Sensors =
        [
            new SensorConfig
            {
                Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate], GridTypes = ["grid-a"]
            }
        ];
        return rule;
    }

    private static PipelineRuleDocument Rule(string id) => new()
    {
        Id = id, RuleName = id,
        Match = new RuleMatchConditions { LocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))" },
        RunParams = [RunParams("tenant-1")]
    };

    private static JsonElement RunParams(string tenant) => JsonSerializer.SerializeToElement(new
    {
        tenantId = tenant, algorithmNames = new[] { "FindAir" },
        tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
    });

    private static JsonObject Overlay() => JsonNode.Parse("""
        {"id":"image-a","sensorName":"camera","sensorType":"EO","registrationQuality":"Accurate",
         "bestResolution":0.7,"imageUrl":"https://example.invalid/image","width":100,"height":100,
         "photoTime":"2020-01-02T10:00:00Z","gridType":"grid-a","gridURI":"https://example.invalid/grid",
         "modeSAR":"stripmap","roiFootprint":{"type":"Polygon","coordinates":[[[1,1],[3,1],[3,3],[1,3],[1,1]]]}}
        """)!.AsObject();

    private static HistoricalRuleTarget Target(string pipelineId, PipelineRuleDocument rule) =>
        new() { PipelineId = pipelineId, Rule = rule };

    private static RabbitMqMessageEnvelope Message(
        PipelineRuleDocument? rule = null, JsonObject? overlay = null,
        List<HistoricalRuleTarget>? targets = null, string runId = "run-a") => new(
        "historical-source",
        JsonSerializer.SerializeToUtf8Bytes(new HistoricalOverlayEnvelope
        {
            RunId = runId,
            Overlay = JsonSerializer.SerializeToElement(overlay ?? Overlay()),
            Targets = targets ?? [Target("pipeline-a", rule ?? Rule("saved-rule"))]
        }),
        Headers: null);

    private static JsonObject EnvelopeNode() => JsonNode.Parse(Message().Body)!.AsObject();

    private static RabbitMqMessageEnvelope RawMessage(string json) => new(
        "source-message", Encoding.UTF8.GetBytes(json), Headers: null);

    private static IReadOnlyDictionary<string, object?> GridUpdatedHeaders() =>
        new Dictionary<string, object?> { ["x-updated-fields"] = new List<object> { Encoding.UTF8.GetBytes("gridType") } };

    private sealed class Gateway : IDisposable
    {
        public Gateway(bool enabled = true, Func<DispatchUnit, DispatchOutcome>? send = null)
        {
            var catalog = CreateCatalog(RabbitMqPipeline("pipeline-a"), RabbitMqPipeline("pipeline-b"),
                RabbitMqPipeline("disabled", enabled: false));
            var contracts = new PipelineContractRegistry([new FindAirPipelineContract()]);
            Cache = new GatewayRuleCache(catalog, catalog, contracts, new LiveRepository(),
                Options.Create(new RuleRefreshOptions { IntervalSeconds = 3600 }), NullLogger<GatewayRuleCache>.Instance);
            var matcher = new RuleMatcher(TimeProvider.System);
            var preparer = new PipelineWorkPreparer(catalog, contracts, NullLogger<PipelineWorkPreparer>.Instance);
            Transport = new RecordingTransport(send ?? (unit => DispatchOutcome.Delivered(unit)));
            var dispatcher = new PipelineDispatcher([Transport], [], NullLogger<PipelineDispatcher>.Instance);
            var historical = new HistoricalSourceMessageHandler(catalog, contracts, matcher, preparer, dispatcher,
                Options.Create(new HistoricalInputOptions { Enabled = enabled }), Logger);
            Handler = new SourceMessageHandler(Cache, matcher, preparer, dispatcher, NullLogger<SourceMessageHandler>.Instance, historical);
        }

        public GatewayRuleCache Cache { get; }
        public RecordingHistoricalLogger Logger { get; } = new();
        public RecordingTransport Transport { get; }
        public SourceMessageHandler Handler { get; }
        public void Dispose() => Cache.Dispose();
    }

    private sealed class LiveRepository : IRuleRepository
    {
        public Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken cancellationToken)
        {
            var rule = Rule("live-rule");
            rule.RunParams = [RunParams("live-tenant")];
            return Task.FromResult(new RuleLoadResult([rule], []));
        }
    }

    private sealed class RecordingTransport(Func<DispatchUnit, DispatchOutcome> send) : IDispatchTransport
    {
        public ConcurrentQueue<DispatchUnit> Sent { get; } = new();
        public PipelineTransportKind Kind => PipelineTransportKind.RabbitMq;

        public Task<DispatchOutcome> SendAsync(DispatchUnit unit, CancellationToken cancellationToken)
        {
            Sent.Enqueue(unit);
            return Task.FromResult(send(unit));
        }
    }

    private sealed class RecordingHistoricalLogger : ILogger<HistoricalSourceMessageHandler>
    {
        private readonly AsyncLocal<Scope?> _scope = new();
        public ConcurrentQueue<(int EventId, IReadOnlyDictionary<string, object?> Fields)> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var previous = _scope.Value;
            _scope.Value = new(state as IEnumerable<KeyValuePair<string, object?>> ?? [], previous);
            return new Restore(() => _scope.Value = previous);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var scope = _scope.Value; scope is not null; scope = scope.Parent)
                foreach (var field in scope.Fields)
                    fields.TryAdd(field.Key, field.Value);
            Entries.Enqueue((eventId.Id, fields));
        }

        private sealed record Scope(IEnumerable<KeyValuePair<string, object?>> Fields, Scope? Parent);
        private sealed class Restore(Action restore) : IDisposable { public void Dispose() => restore(); }
    }
}
