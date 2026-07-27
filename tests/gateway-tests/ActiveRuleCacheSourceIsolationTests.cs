using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Gateway.Tests;

public sealed class ActiveRuleCacheSourceIsolationTests
{
    [Fact]
    public async Task StartupPublishesValidRulesAndSkipsRejectedSources()
    {
        var cache = CreateCache(new RuleLoadResult(
            [ValidRule()],
            [new RuleSourceRejection("invalid-rule", "Invalid Elasticsearch source.")]));

        await cache.StartAsync(CancellationToken.None);
        try
        {
            Assert.Equal("valid-rule", Assert.Single(cache.Current).Id);
        }
        finally
        {
            await cache.StopAsync(CancellationToken.None);
            cache.Dispose();
        }
    }

    [Fact]
    public async Task StartupFailsWhenEverySourceInANonemptyLoadIsRejected()
    {
        var health = new GatewayHealthState();
        var cache = CreateCache(
            new RuleLoadResult(
                [],
                [new RuleSourceRejection("invalid-rule", "Invalid Elasticsearch source.")]),
            health);

        await Assert.ThrowsAsync<InvalidDataException>(
            () => cache.StartAsync(CancellationToken.None));

        Assert.Empty(cache.Current);
        Assert.False(health.RulesLoaded);
        cache.Dispose();
    }

    private static ActiveRuleCache CreateCache(
        RuleLoadResult load,
        GatewayHealthState? health = null) =>
        new(
            new StaticLoadRepository(load),
            new GatewayGeometryConverter(),
            Options.Create(new GatewaySettings
            {
                RuleRefreshIntervalSeconds = 3600
            }),
            health ?? new GatewayHealthState(),
            NullLogger<ActiveRuleCache>.Instance);

    private static RuleDto ValidRule() =>
        new()
        {
            Id = "valid-rule",
            RuleName = "valid-rule",
            AlgorithmNames = [AlgorithmName.FindAir, AlgorithmName.Rpn],
            Sensors = new Dictionary<string, List<RegistrationQuality>>(StringComparer.Ordinal)
            {
                ["camera"] = [RegistrationQuality.Accurate]
            },
            TenantsInfo =
            [
                new TenantInfo
                {
                    TenantId = "tenant-1",
                    TilingConfigs =
                    [
                        new TilingConfig
                        {
                            TileSizeWidth = 512,
                            TileSizeHeight = 512
                        }
                    ]
                }
            ],
            MinimumResolution = 0.5,
            MaximumResolution = 1,
            LocationWkt = "POLYGON ((0 0, 2 0, 2 2, 0 2, 0 0))"
        };

    private sealed class StaticLoadRepository(RuleLoadResult load) : IRuleRepository
    {
        public Task<RuleLoadResult> GetActiveRulesAsync(CancellationToken cancellationToken) =>
            Task.FromResult(load);
    }
}
