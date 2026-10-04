using System.Net;
using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
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
        Assert.False(body.RootElement.GetProperty("dispatchActive").GetBoolean());
        Assert.Equal(new[] { "catalog", "contracts" }, body.RootElement.GetProperty("capabilities")
            .EnumerateArray().Select(capability => capability.GetString()));
        Assert.False(body.RootElement.TryGetProperty("retryWorkerEnabled", out _));
        var source = factory.Services.GetRequiredService<IRuleSourceResolver>().Resolve("asd");
        Assert.Equal("asd-integ-pipeline-index", source.IndexName);
        Assert.Equal("asd", source.PipelineId);
    }

    [Fact]
    public void CatalogRemainsAvailableInternallyWithPrivateSettings()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:asd:ExtraData"] = "{\"internalValue\":\"private\"}"
        });
        using var client = factory.CreateClient();

        var catalog = factory.Services.GetRequiredService<IPipelineCatalog>();
        Assert.Equal(2, catalog.GetAll().Count);
        var pipeline = catalog.GetRequired("asd");
        Assert.Equal("asd", pipeline.PipelineId);
        Assert.Equal("asd", pipeline.ContractId);
        Assert.Equal("private", pipeline.ExtraData.Value.GetProperty("internalValue").GetString());
    }

    [Fact]
    public void UnknownConfiguredContractFailsBeforeHostServesTraffic()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:asd:ContractId"] = "missing"
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public void ValidDisabledPipelineRemainsInCatalogAndIsNotEnabled()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:asd:Enabled"] = "false"
        });
        using var client = factory.CreateClient();

        Assert.False(factory.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").Enabled);
        Assert.DoesNotContain(factory.Services.GetRequiredService<IPipelineCatalog>().GetEnabled(), pipeline => pipeline.PipelineId == "asd");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PipelineMetadataRoutesAreAbsentRegardlessOfEnabledState(bool enabled)
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:asd:Enabled"] = enabled.ToString()
        });
        using var client = factory.CreateClient();

        foreach (var path in new[] { "/pipelines", "/pipelines/asd", "/pipelines/algo", "/pipelines/missing" })
        {
            using var response = await client.GetAsync(path);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public void SharedCatalogIncludesAlgoWithPrivateSettingsAndPassiveHttpDescriptor()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();
        var pipeline = factory.Services.GetRequiredService<IPipelineCatalog>().GetRequired("algo");
        Assert.Equal("algo", pipeline.ContractId);
        Assert.Equal("algo-integ-pipeline-index", pipeline.RulesIndex);
        Assert.Equal("PUT", pipeline.Transport.Http!.Method);
        Assert.True(pipeline.ExtraData.Value.GetProperty("SaveDetections").GetBoolean());
    }

    [Fact]
    public void AlgoSettingsMustPassContractValidationBeforeHostServesTraffic()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:algo:ExtraData"] = "{}"
        });
        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    private static WebApplicationFactory<Program> CreateFactory(Dictionary<string, string?>? settings = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("Observability:Enabled", "false");
            if (settings is not null)
            {
                builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
            }
        });
}
