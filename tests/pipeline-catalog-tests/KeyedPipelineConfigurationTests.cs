using System.Text;
using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class KeyedPipelineConfigurationTests
{
    private const string Settings = """
        {"Enabled":true,"ContractId":"asd","RulesIndex":"rules",
         "Transport":{"Kind":"http","Http":{"Endpoint":"https://example.invalid/process"}}}
        """;

    [Fact]
    public async Task ObjectKeyDefinesRuntimeIdentityWithoutNestedPipelineId()
    {
        using var configuration = BuildConfiguration("{\"ASD_main\":" + Settings + "}");
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<IPipelineCatalog>();

        Assert.Equal("ASD_main", Assert.Single(catalog.GetAll()).PipelineId);
        Assert.Equal("ASD_main", catalog.GetRequired("ASD_main").PipelineId);
        Assert.Throws<KeyNotFoundException>(() => catalog.GetRequired("asd_main"));
        Assert.Equal(new RuleSource("rules", "ASD_main"),
            host.Services.GetRequiredService<IRuleSourceResolver>().Resolve("ASD_main"));
        await host.StopAsync();
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("[{\"PipelineId\":\"asd\"}]")]
    [InlineData("null")]
    [InlineData("true")]
    public void NativeJsonRequiresObjectKeyedByPipelineId(string pipelines)
    {
        var error = Assert.Throws<FormatException>(() => BuildConfiguration(pipelines));
        Assert.Contains("object keyed by pipeline ID", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"asd\":{},\"asd\":{}}")]
    [InlineData("{\"asd\":{\"Enabled\":true},\"ASD\":{\"ContractId\":\"asd\"}}")]
    public void NativeJsonRejectsDuplicateIdsBeforeConfigurationCanMergeThem(string pipelines)
    {
        var error = Assert.Throws<FormatException>(() => BuildConfiguration(pipelines));
        Assert.Contains("duplicate pipeline IDs ignoring case", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"a:b\":{}}")]
    [InlineData("{\"a__b\":{}}")]
    [InlineData("{\"asd_\":{}}")]
    public void NativeJsonRejectsIdsAmbiguousInConfigurationPaths(string pipelines)
    {
        var error = Assert.Throws<FormatException>(() => BuildConfiguration(pipelines));
        Assert.Contains("invalid pipeline ID key", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"asd\":null}")]
    [InlineData("{\"asd\":[]}")]
    [InlineData("{\"asd\":\"ignored\"}")]
    public void NativeJsonRejectsNonobjectPipelineEntries(string pipelines)
    {
        var error = Assert.Throws<FormatException>(() => BuildConfiguration(pipelines));
        Assert.Contains("PipelineCatalog:Pipelines:asd", error.Message, StringComparison.Ordinal);
        Assert.Contains("must be an object", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NestedPipelineIdIsRejectedRatherThanCompetingWithObjectKey()
    {
        using var configuration = BuildConfiguration("{\"asd\":" + Settings + "}");
        configuration["PipelineCatalog:Pipelines:asd:PipelineId"] = "different";
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("PipelineId", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NamedOverrideSelectsItsPipelineAndOmittedEntriesRemainConfigured()
    {
        using var configuration = BuildConfiguration("{\"zeta\":" + Settings + ",\"alpha\":" + Settings + "}",
            builder => builder.AddPipelineCatalogJsonStream(JsonStream("{\"alpha\":{\"Enabled\":false}}")));
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var catalog = host.Services.GetRequiredService<IPipelineCatalog>();

        Assert.Equal(2, catalog.GetAll().Count);
        Assert.Equal("zeta", Assert.Single(catalog.GetEnabled()).PipelineId);
        Assert.False(catalog.GetRequired("alpha").Enabled);
        await host.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseVariantOverridesFailInsteadOfRenamingPipeline(bool useChainedConfiguration)
    {
        using var configuration = BuildConfiguration("{\"asd\":" + Settings + "}", builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineCatalog:Pipelines:ASD:Enabled"] = "false"
            }));
        if (useChainedConfiguration)
        {
            using var host = CreateHost(configuration);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());
            Assert.Contains("differing only in case", error.Message, StringComparison.Ordinal);
        }
        else
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<IPipelineContractRegistry>(new PipelineContractRegistry([new AsdPipelineContract()]));
            services.AddPipelineCatalog(configuration);
            using var provider = services.BuildServiceProvider();
            var error = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IPipelineCatalog>());
            Assert.Contains("differing only in case", error.Message, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("PipelineCatalog:Pipelines")]
    [InlineData("PipelineCatalog:Pipelines:asd")]
    public async Task ScalarOverrideCannotBeIgnoredWhenEarlierObjectChildrenRemain(string key)
    {
        using var configuration = BuildConfiguration("{\"asd\":" + Settings + "}", builder =>
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = "private-invalid-value" }));
        using var host = CreateHost(configuration);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains(key, error.Message, StringComparison.Ordinal);
        Assert.Contains("must be an object", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("private-invalid-value", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EnvironmentOverrideAddressesPipelineByName()
    {
        var prefix = $"CatalogTest_{Guid.NewGuid():N}_";
        var variable = $"{prefix}PipelineCatalog__Pipelines__ASD_main__Enabled";
        Environment.SetEnvironmentVariable(variable, "false");
        try
        {
            using var configuration = BuildConfiguration("{\"ASD_main\":" + Settings + "}",
                builder => builder.AddEnvironmentVariables(prefix));
            using var host = CreateHost(configuration);

            await host.StartAsync();
            var catalog = host.Services.GetRequiredService<IPipelineCatalog>();

            Assert.False(catalog.GetRequired("ASD_main").Enabled);
            Assert.Empty(catalog.GetEnabled());
            await host.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static ConfigurationRoot BuildConfiguration(string pipelines, Action<IConfigurationBuilder>? overrides = null)
    {
        var builder = new ConfigurationBuilder().AddPipelineCatalogJsonStream(JsonStream(pipelines));
        overrides?.Invoke(builder);
        return (ConfigurationRoot)builder.Build();
    }

    private static MemoryStream JsonStream(string pipelines) => new(Encoding.UTF8.GetBytes(
        "{\"PipelineCatalog\":{\"Pipelines\":" + pipelines + "}}"));

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
