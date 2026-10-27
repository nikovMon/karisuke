using System.Net;
using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Source;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class UnifiedGatewayHostTests
{
    [Fact]
    public async Task HostStartsWithoutBrokerOrElasticsearchAndReportsFoundationCapabilities()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/health");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("Healthy", body.RootElement.GetProperty("status").GetString());
        Assert.True(body.RootElement.GetProperty("dispatchActive").GetBoolean());
        Assert.Equal(new[] { "catalog", "contracts", "rules", "dispatch" }, body.RootElement.GetProperty("capabilities")
            .EnumerateArray().Select(capability => capability.GetString()));
        Assert.False(body.RootElement.TryGetProperty("retryWorkerEnabled", out _));
        var source = factory.Services.GetRequiredService<IRuleSourceResolver>().Resolve("FindAir");
        Assert.Equal("asd-integ-pipeline-index", source.IndexName);
        Assert.Equal("FindAir", source.PipelineId);
    }

    [Fact]
    public void CatalogRemainsAvailableInternallyWithPrivateSettings()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:FindAir:ExtraData"] = "{\"internalValue\":\"private\"}"
        });
        using var client = factory.CreateClient();

        var catalog = factory.Services.GetRequiredService<IPipelineCatalog>();
        Assert.Equal(3, catalog.GetAll().Count);
        var pipeline = catalog.GetRequired("FindAir");
        Assert.Equal("FindAir", pipeline.PipelineId);
        Assert.Equal("FindAir", pipeline.ContractId);
        Assert.Equal("private", pipeline.ExtraData.Value.GetProperty("internalValue").GetString());
    }

    [Fact]
    public void UnknownConfiguredContractFailsBeforeHostServesTraffic()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:FindAir:ContractId"] = "missing"
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void ValidDisabledPipelineRemainsInCatalogAndIsNotEnabled()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:FindAir:Enabled"] = "false"
        });
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<IPipelineCatalog>().GetRequired("FindAir").Enabled);
        Assert.DoesNotContain(factory.Services.GetRequiredService<IPipelineCatalog>().GetEnabled(), pipeline => pipeline.PipelineId == "FindAir");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PipelineMetadataRoutesAreAbsentRegardlessOfEnabledState(bool enabled)
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:FindAir:Enabled"] = enabled.ToString()
        });
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/pipelines", "/pipelines/FindAir", "/pipelines/DeepWisdom", "/pipelines/missing" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public void GatewayCatalogIncludesDeepWisdomWithHttpDescriptor()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var pipeline = factory.Services.GetRequiredService<IPipelineCatalog>().GetRequired("DeepWisdom");
        Assert.Equal("DeepWisdom", pipeline.ContractId);
        Assert.Equal("algo-integ-pipeline-index", pipeline.RulesIndex);
        Assert.Equal("PUT", pipeline.Transport.Http!.Method);
        Assert.True(pipeline.ExtraData.Value.GetProperty("SaveDetections").GetBoolean());
    }

    [Fact]
    public void DeepWisdomExtraDataDoesNotRequireNamedSettings()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:DeepWisdom:ExtraData"] = "{}"
        });
        using var client = factory.CreateClient();
        Assert.Empty(factory.Services.GetRequiredService<IPipelineCatalog>()
            .GetRequired("DeepWisdom").ExtraData.Value.EnumerateObject());
    }

    [Fact]
    public void DispatcherResolvesBothCatalogTransportsAndHttpDispatchDoesNotFollowRedirects()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        var dispatcher = factory.Services.GetRequiredService<PipelineDispatcher>();
        var transports = factory.Services.GetServices<IDispatchTransport>().Select(transport => transport.Kind);
        Assert.NotNull(dispatcher);
        Assert.Equal([PipelineTransportKind.RabbitMq, PipelineTransportKind.Http], transports);

        var handler = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>()
            .CreateHandler(HttpDispatchTransport.HttpClientName);
        while (handler is DelegatingHandler delegating)
        {
            handler = delegating.InnerHandler!;
        }

        Assert.False(Assert.IsType<SocketsHttpHandler>(handler).AllowAutoRedirect);
        Assert.Equal(Timeout.InfiniteTimeSpan, factory.Services.GetRequiredService<IHttpClientFactory>()
            .CreateClient(HttpDispatchTransport.HttpClientName).Timeout);
    }

    [Fact]
    public async Task SourceHandlerIsWiredAndAppliesNormalUpdateFiltering()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var handler = factory.Services.GetRequiredService<IRabbitMqMessageHandler>();

        var result = await handler.HandleAsync(RabbitMqMessageEnvelope.FromUtf8(
            """{"id":"image-a"}"""));

        Assert.IsType<SourceMessageHandler>(handler);
        Assert.True(result.IsSuccess);
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Observability:Enabled", "false");
            // Tests have no Elasticsearch or broker: every rule index is empty and nothing is consumed.
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IRuleRepository>();
                services.AddSingleton<IRuleRepository, EmptyRuleRepository>();
                services.Remove(services.Single(service => service.ImplementationType == typeof(RabbitMqConsumerService)));
            });
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            }
        });

    private sealed class EmptyRuleRepository : IRuleRepository
    {
        public Task<RuleLoadResult> GetActiveRulesAsync(string indexName, CancellationToken cancellationToken) =>
            Task.FromResult(new RuleLoadResult([], []));
    }
}
