using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Tests;

public sealed class GatewayWorkerTests
{
    [Fact]
    public async Task HandleAsyncReturnsOneOutputPerMatchedRuleTenant()
    {
        var rule = MatchingRule();
        rule.TenantsInfo =
        [
            Tenant("der", Tiling(5, 5)),
            Tenant("findair", Tiling(10, 10), Tiling(20, 20))
        ];
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage());

        Assert.True(result.IsSuccess);
        Assert.Null(result.OutputBody);
        var outputs = OutputMessages(result);
        Assert.Equal(2, outputs.Count);

        using var first = JsonDocument.Parse(outputs[0].Body);
        using var second = JsonDocument.Parse(outputs[1].Body);
        Assert.Equal("rule-1", first.RootElement.GetProperty("ruleId").GetString());
        Assert.Equal("FindAir", first.RootElement.GetProperty("algorithmName").GetString());
        Assert.Equal("der", first.RootElement.GetProperty("tenantId").GetString());
        Assert.Single(first.RootElement.GetProperty("tilingConfigs").EnumerateArray());
        Assert.Equal("image-1", first.RootElement.GetProperty("imageId").GetString());
        Assert.Equal("2026-06-30T06:54:07+00:00", first.RootElement.GetProperty("photoTime").GetString());
        Assert.Equal("camera", first.RootElement.GetProperty("sensorType").GetString());
        Assert.Equal("Polygon", first.RootElement.GetProperty("roiFootprint").GetProperty("type").GetString());
        Assert.Equal("findair", second.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("tilingConfigs").GetArrayLength());
        Assert.Equal(
            [
                "ruleId",
                "algorithmName",
                "tenantId",
                "tilingConfigs",
                "imageId",
                "roiFootprint",
                "photoTime",
                "sensorType"
            ],
            first.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.All(outputs, output =>
        {
            Assert.Equal("message-1", output.CorrelationId);
        });
        Assert.Equal("message-1:gateway-output:rule-1:der:0", outputs[0].MessageId);
        Assert.Equal("message-1:gateway-output:rule-1:findair:1", outputs[1].MessageId);
    }

    [Fact]
    public async Task HandleAsyncAcknowledgesWithoutPublishingWhenNoRulesMatch()
    {
        var rule = MatchingRule();
        rule.Sensors["camera"] = ["other-camera"];
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage());

        Assert.True(result.IsSuccess);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task ActiveRuleCacheMarksFailedRefreshAndKeepsLastValidSnapshot()
    {
        var health = new GatewayHealthState();
        var geometry = new GatewayGeometryConverter();
        var cache = new ActiveRuleCache(
            new FailingAfterInitialLoadRepository([MatchingRule()]),
            geometry,
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 1
            }),
            health);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            var refreshFailed = await WaitUntilAsync(
                () => health.ConsecutiveRulesRefreshFailures > 0,
                TimeSpan.FromSeconds(3));

            Assert.True(refreshFailed);
            Assert.True(health.RulesLoaded);
            Assert.NotNull(health.LastSuccessfulRulesRefreshAt);
            Assert.NotNull(health.LastFailedRulesRefreshAt);
            Assert.Single(cache.Current);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheSkipsInvalidRulesWhenBuildingSnapshot()
    {
        var invalidRule = MatchingRule();
        invalidRule.Id = "invalid-rule";
        invalidRule.LocationWkt = "not valid wkt";
        var validRule = MatchingRule();
        await using var harness = await GatewayWorkerHarness.CreateAsync([invalidRule, validRule]);

        Assert.Single(harness.RuleCache.Current);
        Assert.Equal("rule-1", harness.RuleCache.Current[0].Id);
    }

    [Fact]
    public async Task ExecuteAsyncRestartsConsumerWhenConsumeAsyncFails()
    {
        var consumer = new FailingThenBlockingConsumer();
        await using var harness = await GatewayWorkerHarness.CreateAsync(
            [MatchingRule()],
            consumer,
            TimeSpan.Zero);

        await harness.GatewayWorker.StartAsync(CancellationToken.None);
        try
        {
            await consumer.SecondCallStarted.WaitAsync(TimeSpan.FromSeconds(1));

            Assert.True(consumer.Calls >= 2);
        }
        finally
        {
            await harness.GatewayWorker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task HandleAsyncDoesNotFallbackToOtherSensorTypesWhenInputHasSensorType()
    {
        var rule = MatchingRule();
        rule.Sensors["camera"] = ["cam-001"];
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage(sensorType: "radar"));

        Assert.True(result.IsSuccess);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureForInvalidInputSoBrokerCanDeadLetter()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);
        var invalid = RabbitMqMessageEnvelope.FromUtf8("""{"sensorName":"cam-001"}""", "message-1");

        var result = await harness.GatewayWorker.HandleAsync(invalid);

        Assert.False(result.IsSuccess);
        Assert.Contains("gateway.missing_image_id", result.Error, StringComparison.Ordinal);
        Assert.Empty(OutputMessages(result));
    }

    private static RabbitMqMessageEnvelope InputMessage(string sensorType = "camera") =>
        RabbitMqMessageEnvelope.FromUtf8(
            $$"""
            {
              "overlay": {
                "id": "image-1"
              },
              "sensorName": "cam-001",
              "sensorType": "{{sensorType}}",
              "bestResolution": 25.9,
              "photoTime": "2026-06-30T06:54:07Z",
              "roiFootprint": {
                "type": "Polygon",
                "coordinates": [
                  [
                    [34.7800, 32.0800],
                    [34.7900, 32.0800],
                    [34.7900, 32.0900],
                    [34.7800, 32.0900],
                    [34.7800, 32.0800]
                  ]
                ]
              }
            }
            """,
            "message-1");

    private static RuleDto MatchingRule() =>
        new()
        {
            Id = "rule-1",
            RuleName = "FindSuspiciousAreaRule",
            Description = "Rule that detects suspicious activity in a configured geographic area",
            AlgorithmName = AlgorithmName.FindAir,
            Sensors = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["camera"] = ["cam-001"]
            },
            IsActive = true,
            TenantsInfo = [Tenant("der", Tiling(5, 5))],
            MinimumResolution = 0.5,
            MaximumResolution = 999,
            Area = "north-zone-a",
            LocationWkt = "POLYGON((34.7800 32.0800, 34.7900 32.0800, 34.7900 32.0900, 34.7800 32.0900, 34.7800 32.0800))",
            CreationTime = DateTimeOffset.UtcNow.AddMinutes(-5),
            UpdateTime = DateTimeOffset.UtcNow.AddMinutes(-5)
        };

    private static TenantInfo Tenant(string tenantId, params TilingConfig[] tilingConfigs) =>
        new()
        {
            TenantId = tenantId,
            TilingConfigs = tilingConfigs.ToList()
        };

    private static TilingConfig Tiling(int width, int height) =>
        new()
        {
            TileSizeWidth = width,
            TileSizeHeight = height
        };

    private static async Task<bool> WaitUntilAsync(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50));
        }

        return predicate();
    }

    private static IReadOnlyList<RabbitMqMessageEnvelope> OutputMessages(RabbitMqMessageProcessingResult result) =>
        result.OutputMessages ?? [];

    private sealed class GatewayWorkerHarness : IAsyncDisposable
    {
        private GatewayWorkerHarness(GatewayWorker gatewayWorker, ActiveRuleCache ruleCache)
        {
            GatewayWorker = gatewayWorker;
            RuleCache = ruleCache;
        }

        public GatewayWorker GatewayWorker { get; }
        public ActiveRuleCache RuleCache { get; }

        public static async Task<GatewayWorkerHarness> CreateAsync(
            IReadOnlyList<RuleDto> rules,
            IRabbitMqConsumer? consumer = null,
            TimeSpan? consumerRestartDelay = null)
        {
            var health = new GatewayHealthState();
            var geometry = new GatewayGeometryConverter();
            var pathReader = new JsonPathReader();
            var inputParser = new GatewayInputMessageParser(
                pathReader,
                geometry);
            var outputBuilder = new GatewayOutputMessageBuilder(geometry);
            var ruleCache = new ActiveRuleCache(
                new StaticRuleRepository(rules),
                geometry,
                Options.Create(new GatewaySettings
                {
                    RuleRefreshIntervalSeconds = 3600
                }),
                health);
            await ruleCache.StartAsync(CancellationToken.None);

            var worker = new GatewayWorker(
                consumer ?? new NoopConsumer(),
                ruleCache,
                inputParser,
                new RuleMatcher(),
                outputBuilder,
                health,
                NullLogger<GatewayWorker>.Instance,
                consumerRestartDelay ?? TimeSpan.FromSeconds(5));

            return new GatewayWorkerHarness(worker, ruleCache);
        }

        public async ValueTask DisposeAsync()
        {
            await RuleCache.StopAsync(CancellationToken.None);
            RuleCache.Dispose();
        }
    }

    private sealed class StaticRuleRepository : IRuleRepository
    {
        private readonly IReadOnlyList<RuleDto> _rules;

        public StaticRuleRepository(IReadOnlyList<RuleDto> rules)
        {
            _rules = rules;
        }

        public Task<IReadOnlyList<RuleDto>> GetActiveRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_rules);
    }

    private sealed class FailingAfterInitialLoadRepository : IRuleRepository
    {
        private readonly IReadOnlyList<RuleDto> _initialRules;
        private int _calls;

        public FailingAfterInitialLoadRepository(IReadOnlyList<RuleDto> initialRules)
        {
            _initialRules = initialRules;
        }

        public Task<IReadOnlyList<RuleDto>> GetActiveRulesAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                return Task.FromResult(_initialRules);
            }

            throw new InvalidOperationException("Refresh failed.");
        }
    }

    private sealed class FailingThenBlockingConsumer : IRabbitMqConsumer
    {
        private readonly TaskCompletionSource _secondCallStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task SecondCallStarted => _secondCallStarted.Task;

        public async Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _calls) == 1)
            {
                throw new InvalidOperationException("Consumer failed.");
            }

            _secondCallStarted.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }
    }

    private sealed class NoopConsumer : IRabbitMqConsumer
    {
        public Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
