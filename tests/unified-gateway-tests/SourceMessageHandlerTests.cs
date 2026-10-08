using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using ImagingPipeline.UnifiedGateway.Rules;
using ImagingPipeline.UnifiedGateway.Source;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class SourceMessageHandlerTests
{
    [Fact]
    public async Task UpdateWithoutGridTypeIsSkippedWithoutLogging()
    {
        await using var gateway = await Gateway.StartAsync([Rule("rule-a")]);

        var result = await gateway.Handler.HandleAsync(Message(updatedFields: ["photoTime"]));

        Assert.True(result.IsSuccess);
        Assert.Empty(gateway.Transport.Sent);
        Assert.Empty(gateway.Logger.Entries);
    }

    [Fact]
    public async Task InvalidMessageIsDeadLetteredWithItsErrorCode()
    {
        await using var gateway = await Gateway.StartAsync([Rule("rule-a")]);

        var result = await gateway.Handler.HandleAsync(Message(body: "{"));

        Assert.False(result.IsSuccess);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        var log = Assert.Single(gateway.Logger.Entries);
        Assert.Equal(6020, log.EventId);
        Assert.Equal("Source message is invalid; it will be dead-lettered.", log.Message);
        Assert.Equal("gateway.invalid_json", log.Fields[TelemetryAttributeNames.ErrorCode]);
    }

    [Fact]
    public async Task MatchedImageIsDispatchedOncePerRunWithTheRuleArea()
    {
        var rule = Rule("rule-a");
        rule.RunParams = [RunParams("tenant-1"), RunParams("tenant-2"), RunParams("tenant-1")];
        await using var gateway = await Gateway.StartAsync([rule, Rule("elsewhere", "POLYGON ((50 50, 51 50, 51 51, 50 51, 50 50))")]);

        var result = await gateway.Handler.HandleAsync(Message());

        Assert.True(result.IsSuccess);
        // The duplicate tenant-1 run collapses into one unit.
        Assert.Equal(2, gateway.Transport.Sent.Count);
        var unit = gateway.Transport.Sent.First();
        Assert.StartsWith("image-a:asd:rule-a:", unit.DispatchId, StringComparison.Ordinal);
        Assert.Equal("source-message", unit.SourceMessageId);
        using var body = JsonDocument.Parse(unit.Work.Payload.Body);
        Assert.Equal(unit.DispatchId, body.RootElement.GetProperty("taskId").GetString());
        Assert.Equal("rule-a", body.RootElement.GetProperty("ruleId").GetString());
        // The ROI is the footprint (1..3) cut to the rule's area (0..2): a 1x1 square, not the 2x2 footprint.
        var roi = GeometryUtilities.ReadGeoJson(body.RootElement.GetProperty("roiFootprint"));
        Assert.Equal(1, roi.Area, precision: 9);

        var evaluated = Assert.Single(gateway.Logger.Entries, entry => entry.EventId == 7002);
        Assert.Equal("asd", evaluated.Fields[TelemetryAttributeNames.PipelineId]);
        Assert.Equal("image-a", evaluated.Fields[TelemetryAttributeNames.PipelineImageId]);
        Assert.Equal(1, evaluated.Fields[TelemetryAttributeNames.RulesMissedGeometryCount]);
        var processed = Assert.Single(gateway.Logger.Entries, entry => entry.EventId == 6021);
        Assert.Equal("Source message processed.", processed.Message);
        Assert.Equal(2, processed.Fields[TelemetryAttributeNames.DispatchUnitCount]);
        Assert.Equal(0, processed.Fields[TelemetryAttributeNames.DispatchFailedCount]);
        Assert.Equal("success", processed.Fields[TelemetryAttributeNames.PipelineOutcome]);
    }

    [Fact]
    public async Task RetryableDispatchFailureRetriesTheMessage()
    {
        await using var gateway = await Gateway.StartAsync(
            [Rule("rule-a")],
            unit => DispatchOutcome.Retryable(unit, TelemetryErrorCategory.Timeout));

        var result = await gateway.Handler.HandleAsync(Message());

        Assert.Equal(RabbitMqMessageFailureAction.Retry, result.FailureAction);
        Assert.Contains("asd", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectedDispatchDeadLettersTheMessage()
    {
        await using var gateway = await Gateway.StartAsync(
            [Rule("rule-a")],
            unit => DispatchOutcome.Rejected(unit, TelemetryErrorCategory.Validation, statusCode: 400));

        var result = await gateway.Handler.HandleAsync(Message());

        Assert.False(result.IsSuccess);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        Assert.Contains("asd", result.Error, StringComparison.Ordinal);
    }

    private static PipelineRuleDocument Rule(string id, string locationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))") => new()
    {
        Id = id,
        RuleName = id,
        Match = new RuleMatchConditions { LocationWkt = locationWkt },
        RunParams = [RunParams("tenant-1")]
    };

    private static JsonElement RunParams(string tenantId) => JsonSerializer.SerializeToElement(new
    {
        tenantId,
        algorithmNames = new[] { "FindAir" },
        tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
    });

    private static RabbitMqMessageEnvelope Message(string? body = null, string[]? updatedFields = null) => new(
        "source-message",
        Encoding.UTF8.GetBytes(body ?? $$$"""
            {"id":"image-a","sensorName":"camera","sensorType":"EO","registrationQuality":"Accurate",
             "bestResolution":0.7,"imageUrl":"https://example.invalid/image","width":100,"height":100,
             "photoTime":"{{{DateTimeOffset.UtcNow.AddHours(-1):O}}}","gridType":"grid-a","gridURI":"https://example.invalid/grid",
             "roiFootprint":{"type":"Polygon","coordinates":[[[1,1],[3,1],[3,3],[1,3],[1,1]]]}}
            """),
        Headers: new Dictionary<string, object?>
        {
            // AMQP delivers header strings as byte arrays.
            ["x-updated-fields"] = (updatedFields ?? ["gridType"]).Select(field => (object)Encoding.UTF8.GetBytes(field)).ToList()
        });

    /// <summary>The handler wired to real rules, matching, preparation and dispatch, with a fake transport.</summary>
    private sealed class Gateway : IAsyncDisposable
    {
        private readonly GatewayRuleCache _cache;

        private Gateway(GatewayRuleCache cache, SourceMessageHandler handler, RecordingTransport transport, RecordingLogger logger)
        {
            _cache = cache;
            Handler = handler;
            Transport = transport;
            Logger = logger;
        }

        public SourceMessageHandler Handler { get; }
        public RecordingTransport Transport { get; }
        public RecordingLogger Logger { get; }

        public static async Task<Gateway> StartAsync(
            IReadOnlyList<PipelineRuleDocument> rules,
            Func<DispatchUnit, DispatchOutcome>? send = null)
        {
            var catalog = CreateCatalog(RabbitMqPipeline("asd"));
            var contracts = new PipelineContractRegistry([new AsdPipelineContract()]);
            var cache = new GatewayRuleCache(
                catalog,
                catalog,
                contracts,
                new StaticRuleRepository(new RuleLoadResult(rules, [])),
                Options.Create(new RuleRefreshOptions { IntervalSeconds = 3600 }),
                NullLogger<GatewayRuleCache>.Instance);
            await cache.StartAsync(CancellationToken.None);

            var transport = new RecordingTransport(send ?? (unit => DispatchOutcome.Delivered(unit)));
            var logger = new RecordingLogger();
            var handler = new SourceMessageHandler(
                cache,
                new RuleMatcher(TimeProvider.System),
                new PipelineWorkPreparer(catalog, contracts, NullLogger<PipelineWorkPreparer>.Instance),
                new PipelineDispatcher([transport], [], NullLogger<PipelineDispatcher>.Instance),
                logger);
            return new Gateway(cache, handler, transport, logger);
        }

        public async ValueTask DisposeAsync()
        {
            await _cache.StopAsync(CancellationToken.None);
            _cache.Dispose();
        }
    }

    private sealed class StaticRuleRepository(RuleLoadResult load) : IRuleRepository
    {
        public Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken cancellationToken) =>
            Task.FromResult(load);
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

    private sealed record LogEntry(int EventId, string Message, IReadOnlyDictionary<string, object?> Fields);

    /// <summary>Records each log entry with the fields of the scopes open when it was written.</summary>
    private sealed class RecordingLogger : ILogger<SourceMessageHandler>
    {
        private readonly AsyncLocal<Scope?> _scope = new();
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var parent = _scope.Value;
            _scope.Value = new Scope(state as IEnumerable<KeyValuePair<string, object?>> ?? [], parent);
            return new Restore(() => _scope.Value = parent);
        }

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
            for (var scope = _scope.Value; scope is not null; scope = scope.Parent)
            {
                foreach (var field in scope.Fields)
                {
                    fields.TryAdd(field.Key, field.Value);
                }
            }

            _entries.Enqueue(new LogEntry(eventId.Id, formatter(state, exception), fields));
        }

        private sealed record Scope(IEnumerable<KeyValuePair<string, object?>> Fields, Scope? Parent);

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
