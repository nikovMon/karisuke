using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class AlgoConfigurationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidContractSettingsFailStartupEvenForDisabledAlgoPipelines(bool enabled)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PipelineCatalog:Pipelines:algo:Enabled"] = enabled.ToString(),
            ["PipelineCatalog:Pipelines:algo:ContractId"] = "algo",
            ["PipelineCatalog:Pipelines:algo:RulesIndex"] = "algo-integ-pipeline-index",
            ["PipelineCatalog:Pipelines:algo:Transport:Kind"] = "http",
            ["PipelineCatalog:Pipelines:algo:Transport:Http:Endpoint"] = "https://example.invalid/work",
            ["PipelineCatalog:Pipelines:algo:ExtraData"] = """
                {"XUserName":"private-user","Origin":"private-origin","QueueType":"private-queue","SaveDetections":"true"}
                """
        });
        builder.Services.AddSingleton<IPipelineContractRegistry>(new PipelineContractRegistry([new AlgoPipelineContract()]));
        builder.Services.AddPipelineCatalog(builder.Configuration);
        using var host = builder.Build();

        var error = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        Assert.Contains("ExtraData", error.Message, StringComparison.Ordinal);
        Assert.Contains("SaveDetections", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-user", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-origin", error.Message, StringComparison.Ordinal);
    }
}
