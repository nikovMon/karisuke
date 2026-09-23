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
    public async Task PipelineMetadataDoesNotExposePhysicalDestinationsOrIndexes()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:0:ExtraData"] = "{\"internalValue\":\"private\"}"
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var pipeline = Assert.Single(body.RootElement.EnumerateArray());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("asd", pipeline.GetProperty("pipelineId").GetString());
        Assert.Equal("asd", pipeline.GetProperty("contractId").GetString());
        Assert.Equal(3, pipeline.EnumerateObject().Count());
        Assert.False(pipeline.TryGetProperty("displayName", out _));
        Assert.False(pipeline.TryGetProperty("transport", out _));
        Assert.False(pipeline.TryGetProperty("rulesIndex", out _));
        Assert.False(pipeline.TryGetProperty("extraData", out _));
        Assert.Equal("private", factory.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd")
            .ExtraData.Value.GetProperty("internalValue").GetString());
    }

    [Fact]
    public void UnknownConfiguredContractFailsBeforeHostServesTraffic()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:0:ContractId"] = "missing"
        });

        Assert.Throws<OptionsValidationException>(() => factory.CreateClient());
    }

    [Fact]
    public async Task ValidDisabledPipelineStaysVisibleAndIsNotEnabled()
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:0:Enabled"] = "false"
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.False(body.RootElement[0].GetProperty("enabled").GetBoolean());
        Assert.Empty(factory.Services.GetRequiredService<IPipelineCatalog>().GetEnabled());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task IndividualPipelineMetadataRemainsOnGatewayIncludingDisabledPipelines(bool enabled)
    {
        using var factory = CreateFactory(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:0:Enabled"] = enabled.ToString()
        });
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/pipelines/asd");
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("asd", body.RootElement.GetProperty("pipelineId").GetString());
        Assert.Equal("asd", body.RootElement.GetProperty("contractId").GetString());
        Assert.Equal(enabled, body.RootElement.GetProperty("enabled").GetBoolean());
        Assert.Equal(3, body.RootElement.EnumerateObject().Count());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pipelines/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/pipelines/ASD")).StatusCode);
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
