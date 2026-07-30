using System.Collections.Concurrent;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Gateway.Messages;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;
using Microsoft.Extensions.Logging;
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

        var inputEnvelope = InputMessage() with
        {
            Headers = new Dictionary<string, object?>
            {
                ["x-pipeline-start-unix-ms"] =
                    DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeMilliseconds(),
                ["business-header"] = "preserved"
            }
        };

        var result = await harness.GatewayWorker.HandleAsync(inputEnvelope);

        Assert.True(result.IsSuccess);
        Assert.Null(result.OutputBody);
        var outputs = OutputMessages(result);
        Assert.Equal(2, outputs.Count);

        using var first = JsonDocument.Parse(outputs[0].Body);
        using var second = JsonDocument.Parse(outputs[1].Body);
        Assert.Equal("message-1:gateway-task:rule-1:der:0", first.RootElement.GetProperty("taskId").GetString());
        Assert.Equal("rule-1", first.RootElement.GetProperty("ruleId").GetString());
        Assert.Equal(
            ["FindAir", "Rpn"],
            first.RootElement.GetProperty("algorithmName")
                .EnumerateArray()
                .Select(value => value.GetString()!)
                .ToArray());
        Assert.Equal("der", first.RootElement.GetProperty("tenantId").GetString());
        Assert.Single(first.RootElement.GetProperty("tilingConfigs").EnumerateArray());
        Assert.Equal("image-1", first.RootElement.GetProperty("imageId").GetString());
        Assert.Equal("2026-06-30T06:54:07+00:00", first.RootElement.GetProperty("photoTime").GetString());
        Assert.Equal("EO", first.RootElement.GetProperty("sensorType").GetString());
        Assert.Equal("/images/image-1.tiff", first.RootElement.GetProperty("imageUrl").GetString());
        Assert.Equal(4096, first.RootElement.GetProperty("imageWidth").GetInt32());
        Assert.Equal(3072, first.RootElement.GetProperty("imageHeight").GetInt32());
        Assert.Equal(0.4, first.RootElement.GetProperty("resolutionMPerPx").GetDouble());
        Assert.Equal("cam-001", first.RootElement.GetProperty("sensorName").GetString());
        Assert.Equal("Polygon", first.RootElement.GetProperty("roiFootprint").GetProperty("type").GetString());
        Assert.Equal("message-1:gateway-task:rule-1:findair:1", second.RootElement.GetProperty("taskId").GetString());
        Assert.Equal("findair", second.RootElement.GetProperty("tenantId").GetString());
        Assert.Equal(2, second.RootElement.GetProperty("tilingConfigs").GetArrayLength());
        Assert.Equal(
            [
                "taskId",
                "ruleId",
                "algorithmName",
                "tenantId",
                "tilingConfigs",
                "imageId",
                "roiFootprint",
                "photoTime",
                "sensorType",
                "imageUrl",
                "imageWidth",
                "imageHeight",
                "resolutionMPerPx",
                "sensorName"
            ],
            first.RootElement.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.All(outputs, output =>
        {
            Assert.Equal("message-1", output.CorrelationId);
            Assert.NotNull(output.Headers);
            Assert.Equal("preserved", output.Headers["business-header"]);
            Assert.True(output.Headers.ContainsKey("x-pipeline-start-unix-ms"));
        });
        Assert.Equal("message-1:gateway-output:rule-1:der:0", outputs[0].MessageId);
        Assert.Equal("message-1:gateway-output:rule-1:findair:1", outputs[1].MessageId);

        var sharedContract = JsonSerializer.Deserialize<GatewayOutputMessageDto>(
            outputs[0].Body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(sharedContract);
        var validationResults = new List<ValidationResult>();
        Assert.True(
            Validator.TryValidateObject(
                sharedContract,
                new ValidationContext(sharedContract),
                validationResults,
                validateAllProperties: true),
            string.Join(" | ", validationResults.Select(result => result.ErrorMessage)));
    }

    [Fact]
    public async Task HandleAsyncCreatesDifferentTaskIdsForDifferentInputMessages()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);

        var firstResult = await harness.GatewayWorker.HandleAsync(InputMessage(messageId: "message-1"));
        var secondResult = await harness.GatewayWorker.HandleAsync(InputMessage(messageId: "message-2"));

        var firstTaskId = OutputTaskId(firstResult);
        var secondTaskId = OutputTaskId(secondResult);
        Assert.Equal("message-1:gateway-task:rule-1:der:0", firstTaskId);
        Assert.Equal("message-2:gateway-task:rule-1:der:0", secondTaskId);
        Assert.NotEqual(firstTaskId, secondTaskId);
    }

    [Fact]
    public async Task HandleAsyncCreatesAggregateParseMatchAndBuildSpans()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.Gateway);
        using var handlerActivity = new Activity("rabbitmq handler").Start();
        handlerActivity.IsAllDataRequested = true;

        var result = await harness.GatewayWorker.HandleAsync(
            InputMessage(messageId: "telemetry-message"));

        Assert.True(result.IsSuccess);
        Assert.Equal(
            ["gateway.parse", "gateway.match", "gateway.build"],
            activities.Activities.Select(activity => activity.DisplayName).ToArray());
        Assert.All(
            activities.Activities,
            activity => Assert.Equal(ActivityKind.Internal, activity.Kind));
        Assert.All(
            activities.Activities,
            activity => Assert.Equal(ActivityStatusCode.Ok, activity.Status));
        Assert.Equal("image-1", handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineImageId));
        Assert.Equal(1, handlerActivity.GetTagItem("imaging_pipeline.gateway.rules.evaluated"));
        Assert.Equal(1, handlerActivity.GetTagItem("imaging_pipeline.gateway.rules.matched"));
        Assert.Equal(1, handlerActivity.GetTagItem("imaging_pipeline.pipeline.output.count"));
        Assert.Null(handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineRuleId));
        Assert.Null(handlerActivity.GetTagItem(TelemetryAttributeNames.PipelineTenantId));
    }

    [Fact]
    public async Task HandleAsyncAcknowledgesWithoutPublishingWhenNoRulesMatch()
    {
        var rule = MatchingRule();
        rule.Sensors.Clear();
        rule.Sensors["other-camera"] = [RegistrationQuality.Accurate];
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage());

        Assert.True(result.IsSuccess);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncAcknowledgesWithoutPublishingWhenResolutionIsOutsideRuleRange()
    {
        var rule = MatchingRule();
        rule.MinimumResolution = 30;
        rule.MaximumResolution = 40;
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage());

        Assert.True(result.IsSuccess);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task ActiveRuleCacheRepositoryFailureKeepsExactSnapshotAndLaterValidRefreshRecovers()
    {
        var initialRule = MatchingRule();
        var recoveredRule = MatchingRule();
        recoveredRule.Id = "recovered-rule";
        var health = new GatewayHealthState();
        var geometry = new GatewayGeometryConverter();
        var repository = new FailingThenValidRefreshRepository(
            [initialRule],
            [recoveredRule]);
        var cache = new ActiveRuleCache(
            repository,
            geometry,
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 1
            }),
            health,
            NullLogger<ActiveRuleCache>.Instance);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            var initialSnapshot = cache.Current;
            var refreshFailed = await WaitUntilAsync(
                () => health.ConsecutiveRulesRefreshFailures > 0,
                TimeSpan.FromSeconds(5));

            Assert.True(refreshFailed);
            Assert.True(health.RulesLoaded);
            Assert.NotNull(health.LastSuccessfulRulesRefreshAt);
            Assert.NotNull(health.LastFailedRulesRefreshAt);
            Assert.Same(initialSnapshot, cache.Current);
            Assert.Equal("rule-1", Assert.Single(cache.Current).Id);

            repository.AllowRecovery();
            var refreshRecovered = await WaitUntilAsync(
                () => health.ConsecutiveRulesRefreshFailures == 0 &&
                      cache.Current.Count == 1 &&
                      cache.Current[0].Id == "recovered-rule",
                TimeSpan.FromSeconds(5));

            Assert.True(refreshRecovered);
            Assert.NotSame(initialSnapshot, cache.Current);
            Assert.Equal("recovered-rule", Assert.Single(cache.Current).Id);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheRefreshSkipsInvalidRuleAndPublishesValidRules()
    {
        var initialRule = MatchingRule();
        var invalidRule = MatchingRule();
        invalidRule.Id = "invalid-rule";
        invalidRule.LocationWkt = "POLYGON EMPTY";
        var validRule = MatchingRule();
        validRule.Id = "valid-rule";
        var health = new GatewayHealthState();
        var logger = new RecordingLogger<ActiveRuleCache>();
        var repository = new InvalidThenValidRefreshRepository(
            [initialRule],
            [validRule, invalidRule],
            [validRule]);
        var cache = new ActiveRuleCache(
            repository,
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 1
            }),
            health,
            logger);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            var initialSnapshot = cache.Current;
            var validSubsetPublished = await WaitUntilAsync(
                () => cache.Current.Count == 1 &&
                      cache.Current[0].Id == "valid-rule",
                TimeSpan.FromSeconds(5));

            Assert.True(validSubsetPublished);
            Assert.NotSame(initialSnapshot, cache.Current);
            Assert.Equal(0, health.ConsecutiveRulesRefreshFailures);
            Assert.Contains(
                logger.Entries,
                entry =>
                    entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("invalid-rule", StringComparison.Ordinal));
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheStartupSkipsInvalidRuleAndPublishesValidRules()
    {
        var invalidRule = MatchingRule();
        invalidRule.Id = "invalid-rule";
        invalidRule.MinimumResolution = 0;
        var validRule = MatchingRule();
        var health = new GatewayHealthState();
        var logger = new RecordingLogger<ActiveRuleCache>();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([validRule, invalidRule]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health,
            logger);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("rule-1", Assert.Single(cache.Current).Id);
            Assert.True(health.RulesLoaded);
            Assert.Contains(
                logger.Entries,
                entry =>
                    entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("invalid-rule", StringComparison.Ordinal));
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Theory]
    [InlineData("not valid wkt")]
    [InlineData("POINT EMPTY")]
    [InlineData("POLYGON((0 0, 2 2, 0 2, 2 0, 0 0))")]
    public async Task ActiveRuleCacheRejectsAllInvalidRulesDuringStartup(
        string invalidWkt)
    {
        var invalidRule = MatchingRule();
        invalidRule.Id = "invalid-rule";
        invalidRule.LocationWkt = invalidWkt;
        var health = new GatewayHealthState();
        var logger = new RecordingLogger<ActiveRuleCache>();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([invalidRule]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health,
            logger);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.StartAsync(CancellationToken.None));

        Assert.Empty(cache.Current);
        Assert.False(health.RulesLoaded);
        Assert.Null(health.LastSuccessfulRulesRefreshAt);
        Assert.Contains(
            logger.Entries,
            entry =>
                entry.Level == LogLevel.Warning &&
                entry.Message.Contains("none passed validation", StringComparison.Ordinal));
        cache.Dispose();
    }

    [Fact]
    public async Task ActiveRuleCacheAllInvalidRefreshKeepsPreviousSnapshot()
    {
        var initialRule = MatchingRule();
        var invalidRule = MatchingRule();
        invalidRule.Id = "invalid-rule";
        invalidRule.LocationWkt = "POLYGON EMPTY";
        var health = new GatewayHealthState();
        var logger = new RecordingLogger<ActiveRuleCache>();
        var repository = new InvalidThenValidRefreshRepository(
            [initialRule],
            [invalidRule],
            [initialRule]);
        var cache = new ActiveRuleCache(
            repository,
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 1
            }),
            health,
            logger);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            var initialSnapshot = cache.Current;
            var refreshRejected = await WaitUntilAsync(
                () => health.ConsecutiveRulesRefreshFailures > 0,
                TimeSpan.FromSeconds(5));

            Assert.True(refreshRejected);
            Assert.Same(initialSnapshot, cache.Current);
            Assert.Equal("rule-1", Assert.Single(cache.Current).Id);
            Assert.Contains(
                logger.Entries,
                entry =>
                    entry.Level == LogLevel.Warning &&
                    entry.Message.Contains("none passed validation", StringComparison.Ordinal));

            repository.AllowRecovery();
            var refreshRecovered = await WaitUntilAsync(
                () => health.ConsecutiveRulesRefreshFailures == 0 &&
                      !ReferenceEquals(initialSnapshot, cache.Current),
                TimeSpan.FromSeconds(5));

            Assert.True(refreshRecovered);
            Assert.Equal("rule-1", Assert.Single(cache.Current).Id);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheEmptyStartupPublishesEmptySnapshot()
    {
        var health = new GatewayHealthState();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health,
            NullLogger<ActiveRuleCache>.Instance);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            Assert.Empty(cache.Current);
            Assert.True(health.RulesLoaded);
            Assert.Equal(0, health.ConsecutiveRulesRefreshFailures);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheEmptyRefreshPublishesEmptySnapshot()
    {
        var initialRule = MatchingRule();
        var health = new GatewayHealthState();
        var repository = new InvalidThenValidRefreshRepository(
            [initialRule],
            [],
            [initialRule]);
        var cache = new ActiveRuleCache(
            repository,
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 1
            }),
            health,
            NullLogger<ActiveRuleCache>.Instance);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            var initialSnapshot = cache.Current;
            var emptySnapshotPublished = await WaitUntilAsync(
                () => cache.Current.Count == 0,
                TimeSpan.FromSeconds(5));

            Assert.True(emptySnapshotPublished);
            Assert.NotSame(initialSnapshot, cache.Current);
            Assert.True(health.RulesLoaded);
            Assert.Equal(0, health.ConsecutiveRulesRefreshFailures);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task ActiveRuleCacheCanceledStartupDoesNotPublishSnapshot()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var health = new GatewayHealthState();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([MatchingRule()]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health,
            NullLogger<ActiveRuleCache>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.StartAsync(cancellation.Token));

        Assert.Empty(cache.Current);
        Assert.False(health.RulesLoaded);
        cache.Dispose();
    }

    [Fact]
    public async Task ActiveRuleCacheMarksInitialRefreshCancellationOnSpan()
    {
        using var activities = new TelemetryActivityCollector(TelemetrySourceNames.Gateway);
        using var cache = new ActiveRuleCache(
            new CancellingRuleRepository(),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            new GatewayHealthState(),
            NullLogger<ActiveRuleCache>.Instance);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.StartAsync(CancellationToken.None));

        var activity = Assert.Single(activities.Activities);
        Assert.Equal("gateway.rule_cache.refresh", activity.DisplayName);
        Assert.Equal(ActivityStatusCode.Error, activity.Status);
        Assert.Equal(
            "cancelled",
            activity.GetTagItem(TelemetryAttributeNames.ErrorCategory));
    }

    [Fact]
    public async Task ActiveRuleCacheBoundsDetailedInvalidRuleWarnings()
    {
        var invalidRules = Enumerable.Range(1, 12)
            .Select(index =>
            {
                var rule = MatchingRule();
                rule.Id = $"invalid-rule-{index}";
                rule.MinimumResolution = 0;
                return rule;
            })
            .ToArray();
        var validRule = MatchingRule();
        var logger = new RecordingLogger<ActiveRuleCache>();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([.. invalidRules, validRule]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            new GatewayHealthState(),
            logger);

        await cache.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("rule-1", Assert.Single(cache.Current).Id);
            Assert.Equal(
                10,
                logger.Entries.Count(entry =>
                    entry.Message.StartsWith(
                        "Skipping invalid active rule",
                        StringComparison.Ordinal)));
            Assert.Contains(
                logger.Entries,
                entry =>
                    entry.Message.Contains(
                        "skipped 12 invalid rules",
                        StringComparison.Ordinal) &&
                    entry.Message.Contains(
                        "suppressed 2",
                        StringComparison.Ordinal));
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ActiveRuleCacheRejectsInvalidSensorCollections(bool nullSensors)
    {
        var invalidRule = MatchingRule();
        if (nullSensors)
        {
            invalidRule.Sensors = null!;
        }
        else
        {
            invalidRule.Sensors["cam-001"] = [];
        }

        var health = new GatewayHealthState();
        var cache = new ActiveRuleCache(
            new StaticRuleRepository([invalidRule]),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health,
            NullLogger<ActiveRuleCache>.Instance);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.StartAsync(CancellationToken.None));

        Assert.Empty(cache.Current);
        Assert.False(health.RulesLoaded);
        Assert.Null(health.LastSuccessfulRulesRefreshAt);
        cache.Dispose();
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
    public async Task HandleAsyncRequiresMatchingRegistrationQualityForSensorName()
    {
        var rule = MatchingRule();
        rule.Sensors["cam-001"] = [RegistrationQuality.Sensor];
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage(registrationQuality: "Accurate"));

        Assert.True(result.IsSuccess);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncMatchesAnySensorWhenRuleSensorsAreEmpty()
    {
        var rule = MatchingRule();
        rule.Sensors.Clear();
        await using var harness = await GatewayWorkerHarness.CreateAsync([rule]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage(
            sensorName: "unknown-sensor",
            registrationQuality: "Sensor"));

        Assert.True(result.IsSuccess);
        Assert.Single(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureForInvalidInputSoBrokerCanDeadLetter()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);
        var invalid = RabbitMqMessageEnvelope.FromUtf8("""{"sensorName":"cam-001"}""", "message-1");

        var result = await harness.GatewayWorker.HandleAsync(invalid);

        Assert.False(result.IsSuccess);
        Assert.Contains("gateway.invalid_json", result.Error, StringComparison.Ordinal);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenRegistrationQualityIsMissing()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);
        var invalid = RabbitMqMessageEnvelope.FromUtf8(
            """
            {
              "overlay": {
                "id": "image-1",
                "sensorName": "cam-001",
                "sensorType": "EO",
                "bestResolution": 25.9,
                "resolutionMPerPx": 0.4,
                "imageUrl": "/images/image-1.tiff",
                "imageWidth": 4096,
                "imageHeight": 3072,
                "photoTime": "2026-06-30T06:54:07Z",
                "roiFootprint": {
                  "type": "Polygon",
                  "coordinates": [[[34.7800, 32.0800], [34.7900, 32.0800], [34.7900, 32.0900], [34.7800, 32.0900], [34.7800, 32.0800]]]
                }
              }
            }
            """,
            "message-1");

        var result = await harness.GatewayWorker.HandleAsync(invalid);

        Assert.False(result.IsSuccess);
        Assert.Contains("gateway.invalid_json", result.Error, StringComparison.Ordinal);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        Assert.Empty(OutputMessages(result));
    }

    [Fact]
    public async Task HandleAsyncReturnsFailureWhenRegistrationQualityIsInvalid()
    {
        await using var harness = await GatewayWorkerHarness.CreateAsync([MatchingRule()]);

        var result = await harness.GatewayWorker.HandleAsync(InputMessage(registrationQuality: "accurate"));

        Assert.False(result.IsSuccess);
        Assert.Contains("gateway.invalid_registration_quality", result.Error, StringComparison.Ordinal);
        Assert.Equal(RabbitMqMessageFailureAction.DeadLetter, result.FailureAction);
        Assert.Empty(OutputMessages(result));
    }

    private static RabbitMqMessageEnvelope InputMessage(
        string sensorName = "cam-001",
        string registrationQuality = "Accurate",
        string messageId = "message-1") =>
        RabbitMqMessageEnvelope.FromUtf8(
            $$"""
            {
              "overlay": {
                "id": "image-1",
                "sensorName": "{{sensorName}}",
                "sensorType": "EO",
                "registrationQuality": "{{registrationQuality}}",
                "bestResolution": 25.9,
                "resolutionMPerPx": 0.4,
                "imageUrl": "/images/image-1.tiff",
                "imageWidth": 4096,
                "imageHeight": 3072,
                "photoTime": "2026-06-30T06:54:07Z",
                "roiFootprint": {
                  "type": "Polygon",
                  "coordinates": [[[34.7800, 32.0800], [34.7900, 32.0800], [34.7900, 32.0900], [34.7800, 32.0900], [34.7800, 32.0800]]]
                }
              }
            }
            """,
            messageId);

    private static RuleDto MatchingRule() =>
        new()
        {
            Id = "rule-1",
            RuleName = "FindSuspiciousAreaRule",
            Description = "Rule that detects suspicious activity in a configured geographic area",
            AlgorithmNames = [AlgorithmName.FindAir, AlgorithmName.Rpn],
            Sensors = new Dictionary<string, List<RegistrationQuality>>(StringComparer.Ordinal)
            {
                ["cam-001"] = [RegistrationQuality.Accurate]
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

    private static string? OutputTaskId(RabbitMqMessageProcessingResult result)
    {
        var output = Assert.Single(OutputMessages(result));
        using var document = JsonDocument.Parse(output.Body);
        return document.RootElement.GetProperty("taskId").GetString();
    }

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
            var inputParser = new GatewayInputMessageParser(geometry);
            var outputBuilder = new GatewayOutputMessageBuilder(geometry);
            var ruleCache = new ActiveRuleCache(
                new StaticRuleRepository(rules),
                geometry,
                Options.Create(new GatewaySettings
                {
                    RuleRefreshIntervalSeconds = 3600
                }),
                health,
                NullLogger<ActiveRuleCache>.Instance);
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

        public Task<RuleLoadResult> GetActiveRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(RuleLoadResult.FromRules(_rules));
    }

    private sealed class FailingThenValidRefreshRepository : IRuleRepository
    {
        private readonly IReadOnlyList<RuleDto> _initialRules;
        private readonly IReadOnlyList<RuleDto> _validRefreshRules;
        private readonly TaskCompletionSource _recoveryAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public FailingThenValidRefreshRepository(
            IReadOnlyList<RuleDto> initialRules,
            IReadOnlyList<RuleDto> validRefreshRules)
        {
            _initialRules = initialRules;
            _validRefreshRules = validRefreshRules;
        }

        public void AllowRecovery() => _recoveryAllowed.TrySetResult();

        public async Task<RuleLoadResult> GetActiveRulesAsync(
            CancellationToken cancellationToken)
        {
            switch (Interlocked.Increment(ref _calls))
            {
                case 1:
                    return RuleLoadResult.FromRules(_initialRules);
                case 2:
                    throw new InvalidOperationException("Rule repository refresh failed.");
                default:
                    await _recoveryAllowed.Task.WaitAsync(cancellationToken);
                    return RuleLoadResult.FromRules(_validRefreshRules);
            }
        }
    }

    private sealed class InvalidThenValidRefreshRepository : IRuleRepository
    {
        private readonly IReadOnlyList<RuleDto> _initialRules;
        private readonly IReadOnlyList<RuleDto> _invalidRefreshRules;
        private readonly IReadOnlyList<RuleDto> _validRefreshRules;
        private readonly TaskCompletionSource _recoveryAllowed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _calls;

        public InvalidThenValidRefreshRepository(
            IReadOnlyList<RuleDto> initialRules,
            IReadOnlyList<RuleDto> invalidRefreshRules,
            IReadOnlyList<RuleDto> validRefreshRules)
        {
            _initialRules = initialRules;
            _invalidRefreshRules = invalidRefreshRules;
            _validRefreshRules = validRefreshRules;
        }

        public void AllowRecovery() => _recoveryAllowed.TrySetResult();

        public async Task<RuleLoadResult> GetActiveRulesAsync(CancellationToken cancellationToken)
        {
            switch (Interlocked.Increment(ref _calls))
            {
                case 1:
                    return RuleLoadResult.FromRules(_initialRules);
                case 2:
                    return RuleLoadResult.FromRules(_invalidRefreshRules);
                default:
                    await _recoveryAllowed.Task.WaitAsync(cancellationToken);
                    return RuleLoadResult.FromRules(_validRefreshRules);
            }
        }
    }

    private sealed class CancellingRuleRepository : IRuleRepository
    {
        public Task<RuleLoadResult> GetActiveRulesAsync(
            CancellationToken cancellationToken) =>
            Task.FromCanceled<RuleLoadResult>(
                new CancellationToken(canceled: true));
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

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Gateway does not consume in batch mode.");
    }

    private sealed class NoopConsumer : IRabbitMqConsumer
    {
        public Task ConsumeAsync(IRabbitMqMessageHandler handler, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task ConsumeBatchAsync(
            IRabbitMqBatchMessageHandler handler,
            int batchSize,
            TimeSpan maxWaitTime,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull =>
            null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Entries.Enqueue(new LogEntry(
                logLevel,
                formatter(state, exception),
                exception));
    }

    private sealed record LogEntry(
        LogLevel Level,
        string Message,
        Exception? Exception);
}
