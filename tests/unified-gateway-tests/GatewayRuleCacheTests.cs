using System.Collections.Concurrent;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.UnifiedGateway.Rules;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using static ImagingPipeline.UnifiedGateway.Tests.DispatchTestData;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class GatewayRuleCacheTests
{
    [Fact]
    public async Task StartupLoadsEveryEnabledPipelineFromItsOwnIndex()
    {
        var repository = new FakeRuleRepository(_ => Load(Rule("rule-a")));
        using var cache = Cache(repository, RabbitMqPipeline("asd"), HttpPipeline("other"), RabbitMqPipeline("off", enabled: false));

        await cache.StartAsync(CancellationToken.None);

        Assert.Equal(["asd-pipeline-index", "other-pipeline-index"], repository.Indexes.Order());
        Assert.Equal(["asd", "other"], cache.Current.Select(rules => rules.Pipeline.PipelineId).Order());
        Assert.All(cache.Current, rules => Assert.Equal("rule-a", Assert.Single(rules.Rules).Id));
        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task InvalidRuleIsLeftOutAndLoggedWithItsIdAndReason()
    {
        var invalid = Rule("invalid");
        invalid.RunParams = [JsonSerializer.SerializeToElement(new { tenantId = "" })];
        var logger = new RecordingLogger();
        using var cache = Cache(new FakeRuleRepository(_ => Load(Rule("valid"), invalid)), logger, RabbitMqPipeline("asd"));

        await cache.StartAsync(CancellationToken.None);

        Assert.Equal("valid", Assert.Single(Assert.Single(cache.Current).Rules).Id);
        var rejected = Assert.Single(logger.Entries, entry => entry.EventId == 6012);
        Assert.Equal("Pipeline rule rejected; it will not be matched.", rejected.Message);
        Assert.Equal("invalid", rejected.Fields[TelemetryAttributeNames.PipelineRuleId]);
        Assert.Contains("runParams[0].tenantId", (string)rejected.Fields[TelemetryAttributeNames.RuleRejectionReason]!, StringComparison.Ordinal);
        Assert.Equal("asd", rejected.Fields[TelemetryAttributeNames.PipelineId]);
        var loaded = Assert.Single(logger.Entries, entry => entry.EventId == 6010);
        Assert.Equal(1, loaded.Fields[TelemetryAttributeNames.RulesLoadedCount]);
        Assert.Equal(1, loaded.Fields[TelemetryAttributeNames.RulesRejectedCount]);
        await cache.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StartupFailsWhenAPipelineCannotLoad()
    {
        var logger = new RecordingLogger();
        var failure = new RuleLoadException("search failed", new InvalidOperationException());
        using var cache = Cache(new FakeRuleRepository(_ => throw failure), logger, RabbitMqPipeline("asd"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.StartAsync(CancellationToken.None));

        var failed = Assert.Single(logger.Entries, entry => entry.EventId == 6013);
        Assert.Same(failure, failed.Exception);
        Assert.Equal(0, failed.Fields[TelemetryAttributeNames.RulesRetainedCount]);
    }

    [Fact]
    public async Task StartupFailsWhenEveryLoadedRuleIsRejected()
    {
        var invalid = Rule("invalid");
        invalid.MatchAll = false;
        using var cache = Cache(new FakeRuleRepository(_ => Load(invalid)), RabbitMqPipeline("asd"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => cache.StartAsync(CancellationToken.None));
    }

    [Fact]
    public async Task FailedRefreshKeepsThePreviousRules()
    {
        var loads = 0;
        var repository = new FakeRuleRepository(_ => Interlocked.Increment(ref loads) == 1
            ? Load(Rule("first"))
            : throw new RuleLoadException("search failed", new InvalidOperationException()));
        var logger = new RecordingLogger();
        using var cache = Cache(repository, logger, RabbitMqPipeline("asd"));

        await cache.StartAsync(CancellationToken.None);
        var failed = await WaitForLogAsync(logger, 6013);
        await cache.StopAsync(CancellationToken.None);

        Assert.Equal(1, failed.Fields[TelemetryAttributeNames.RulesRetainedCount]);
        Assert.Equal("first", Assert.Single(Assert.Single(cache.Current).Rules).Id);
    }

    // The refresh runs in the background after the configured one-second interval.
    private static async Task<LogEntry> WaitForLogAsync(RecordingLogger logger, int eventId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (logger.Entries.FirstOrDefault(entry => entry.EventId == eventId) is { } entry)
            {
                return entry;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"No log with event {eventId} was written.");
    }

    private static GatewayRuleCache Cache(FakeRuleRepository repository, params ImagingPipeline.PipelineCatalog.PipelineDefinition[] pipelines) =>
        Cache(repository, new RecordingLogger(), pipelines);

    private static GatewayRuleCache Cache(
        FakeRuleRepository repository,
        RecordingLogger logger,
        params ImagingPipeline.PipelineCatalog.PipelineDefinition[] pipelines)
    {
        var catalog = CreateCatalog(pipelines);
        return new GatewayRuleCache(
            catalog,
            catalog,
            new PipelineContractRegistry([new AsdPipelineContract()]),
            repository,
            Options.Create(new RuleRefreshOptions { IntervalSeconds = 1, JitterSeconds = 0 }),
            logger);
    }

    private static RuleLoadResult Load(params PipelineRuleDocument[] rules) => new(rules, []);

    private static PipelineRuleDocument Rule(string id) => new()
    {
        Id = id,
        RuleName = id,
        MatchAll = true,
        RunParams =
        [
            JsonSerializer.SerializeToElement(new
            {
                tenantId = "tenant-1",
                algorithmNames = new[] { "FindAir" },
                tilingConfigs = new[] { new { tileSizeWidth = 512, tileSizeHeight = 512, tileOverlapWidth = 0, tileOverlapHeight = 0 } }
            })
        ]
    };

    private sealed class FakeRuleRepository(Func<string, RuleLoadResult> load) : IRuleRepository
    {
        public ConcurrentQueue<string> Indexes { get; } = new();

        public Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken cancellationToken)
        {
            Indexes.Enqueue(indexName);
            return Task.FromResult(load(indexName));
        }
    }

    private sealed record LogEntry(int EventId, string Message, Exception? Exception, IReadOnlyDictionary<string, object?> Fields);

    /// <summary>Records each log entry with the fields of the scopes open when it was written.</summary>
    private sealed class RecordingLogger : ILogger<GatewayRuleCache>
    {
        private readonly AsyncLocal<ImmutableScope?> _scope = new();
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull
        {
            var parent = _scope.Value;
            _scope.Value = new ImmutableScope(state as IEnumerable<KeyValuePair<string, object?>> ?? [], parent);
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

            _entries.Enqueue(new LogEntry(eventId.Id, formatter(state, exception), exception, fields));
        }

        private sealed record ImmutableScope(IEnumerable<KeyValuePair<string, object?>> Fields, ImmutableScope? Parent);

        private sealed class Restore(Action restore) : IDisposable
        {
            public void Dispose() => restore();
        }
    }
}
