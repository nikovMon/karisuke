using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class DeepWisdomConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ArbitraryExtraDataDoesNotImposeASettingsSchema(bool enabled)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:algo:Enabled"] = enabled.ToString(),
            ["PipelineCatalog:Pipelines:algo:ContractId"] = "DeepWisdom",
            ["PipelineCatalog:Pipelines:algo:RulesIndex"] = "algo-integ-pipeline-index",
            ["PipelineCatalog:Pipelines:algo:Transport:Kind"] = "http",
            ["PipelineCatalog:Pipelines:algo:Transport:Http:Endpoint"] = "https://example.invalid/work",
            ["PipelineCatalog:Pipelines:algo:ExtraData"] = """
                {"XUserName":"private-user","Origin":"private-origin","QueueType":"private-queue","SaveDetections":"true"}
                """
        });
        builder.Services.AddSingleton<IPipelineContractRegistry>(new PipelineContractRegistry([new DeepWisdomPipelineContract()]));
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        await host.StartAsync();
        var pipeline = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("algo");
        Assert.Equal(enabled, pipeline.Enabled);
        Assert.Equal("true", pipeline.ExtraData.Value.GetProperty("SaveDetections").GetString());
        await host.StopAsync();
    }
}
