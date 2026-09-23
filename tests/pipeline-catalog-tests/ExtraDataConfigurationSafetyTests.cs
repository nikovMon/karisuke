using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class ExtraDataConfigurationSafetyTests
{
    private const string ExtraDataKey = "PipelineCatalog:Pipelines:0:ExtraData";

    [Theory]
    [InlineData("[\"private-marker\"]")]
    [InlineData("{\"secret\":\"private-marker\"")]
    [InlineData("\"private-marker\"")]
    public async Task InvalidOverrideDoesNotExposeBodyValuesAnywhereInStartupException(string value)
    {
        using var configuration = Configuration(new Dictionary<string, string?> { [ExtraDataKey] = value });
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(ExtraDataKey, error.Message, StringComparison.Ordinal);
        Assert.Contains("JSON object", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.ToString(), StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("true")]
    [InlineData("42")]
    public async Task PresentNonobjectOverrideIsRejectedInsteadOfFallingBackToDefault(string? value)
    {
        using var configuration = Configuration(new Dictionary<string, string?> { [ExtraDataKey] = value });
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(ExtraDataKey, error.Message, StringComparison.Ordinal);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task HierarchicalOverrideIsRejectedBeforeBinderCanIgnoreIt()
    {
        using var configuration = Configuration(new Dictionary<string, string?>
        {
            [$"{ExtraDataKey}:flag"] = "private-marker"
        });
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("child-key overrides", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task SparsePipelineIndexCannotHideHierarchicalExtraDataOverride()
    {
        const string pipeline = "PipelineCatalog:Pipelines:5";
        using var configuration = Configuration(new Dictionary<string, string?>
        {
            [$"{pipeline}:PipelineId"] = "second",
            [$"{pipeline}:ContractId"] = "asd",
            [$"{pipeline}:Enabled"] = "true",
            [$"{pipeline}:RulesIndex"] = "second-integ-pipeline-index",
            [$"{pipeline}:Transport:Kind"] = "http",
            [$"{pipeline}:Transport:Http:Endpoint"] = "https://service.example/second",
            [$"{pipeline}:ExtraData"] = "{}",
            [$"{pipeline}:ExtraData:flag"] = "private-marker"
        });
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains($"{pipeline}:ExtraData", error.Message, StringComparison.Ordinal);
        Assert.Contains("child-key overrides", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-marker", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrevalidationUsesEffectiveWholeObjectValueFromHighestPriorityProvider()
    {
        var earlier = BaseConfiguration();
        earlier[ExtraDataKey] = "[\"private-marker\"]";
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection(earlier)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ExtraDataKey] = """{"number":1,"numericString":"1","empty":[],"nothing":null}"""
            }).Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var data = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").ExtraData.Value;

        Assert.Equal(1, data.GetProperty("number").GetInt32());
        Assert.Equal("1", data.GetProperty("numericString").GetString());
        Assert.Equal(0, data.GetProperty("empty").GetArrayLength());
        Assert.Equal(System.Text.Json.JsonValueKind.Null, data.GetProperty("nothing").ValueKind);
        await host.StopAsync();
    }

    private static ConfigurationRoot Configuration(Dictionary<string, string?> overrides) =>
        (ConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection(BaseConfiguration())
            .AddInMemoryCollection(overrides)
            .Build();

    private static Dictionary<string, string?> BaseConfiguration() => new()
    {
        ["PipelineCatalog:Pipelines:0:PipelineId"] = "asd",
        ["PipelineCatalog:Pipelines:0:ContractId"] = "asd",
        ["PipelineCatalog:Pipelines:0:Enabled"] = "true",
        ["PipelineCatalog:Pipelines:0:RulesIndex"] = "asd-integ-pipeline-index",
        ["PipelineCatalog:Pipelines:0:Transport:Kind"] = "http",
        ["PipelineCatalog:Pipelines:0:Transport:Http:Endpoint"] = "https://service.example/process",
        [ExtraDataKey] = "{}"
    };

    private static IHost CreateHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IPipelineContractRegistry>(new PipelineContractRegistry([new AsdPipelineContract()]));
        builder.Services.AddPipelineCatalog(builder.Configuration);
        return builder.Build();
    }
}
