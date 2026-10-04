using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ImagingPipeline.UnifiedGateway.Tests;

[Collection("Gateway configuration environment")]
public sealed class GatewayApplicationBuilderTests
{
    [Fact]
    public async Task CatalogPreservesCaseDistinctAndColonKeysAndJsonTypesInExtraData()
    {
        using var files = new RuntimeFiles();
        files.Write("pipelinecatalog.json", Configuration("""
            {
              "Name": "upper",
              "name": "lower",
              "key:part": "literal",
              "key": { "part": "nested" },
              "number": 123,
              "numericString": "123",
              "items": [true, null, {}, []]
            }
            """));
        var builder = GatewayApplicationBuilder.Create(["--PipelineCatalogFile", files.CatalogPath], files.Options());
        RegisterCatalog(builder);
        await using var app = builder.Build();

        var data = app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").ExtraData.Value;

        Assert.Equal("upper", data.GetProperty("Name").GetString());
        Assert.Equal("lower", data.GetProperty("name").GetString());
        Assert.Equal("literal", data.GetProperty("key:part").GetString());
        Assert.Equal("nested", data.GetProperty("key").GetProperty("part").GetString());
        Assert.Equal(123, data.GetProperty("number").GetInt32());
        Assert.Equal("123", data.GetProperty("numericString").GetString());
        Assert.True(data.GetProperty("items")[0].GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("items")[1].ValueKind);
        Assert.Equal(JsonValueKind.Object, data.GetProperty("items")[2].ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("items")[3].ValueKind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StandardJsonFilesAndCommandLineKeepTheirOverridePrecedence(bool commandLineOverride)
    {
        using var files = new RuntimeFiles();
        files.Write("pipelinecatalog.json", Configuration("""{"winner":"catalog","catalogOnly":true}"""));
        files.Write("appsettings.json", ExtraDataOverride("""{"winner":"base","baseOnly":true}"""));
        files.Write("appsettings.Testing.json", ExtraDataOverride("""{"winner":"environmentFile","items":[]}"""));
        string[] args = commandLineOverride
            ? ["--PipelineCatalog:Pipelines:test:ExtraData", """{"winner":"commandLine","enabled":false}"""]
            : [];
        var builder = GatewayApplicationBuilder.Create(["--PipelineCatalogFile", files.CatalogPath, .. args], files.Options());
        RegisterCatalog(builder);
        await using var app = builder.Build();

        var data = app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").ExtraData.Value;

        Assert.Equal(commandLineOverride ? "commandLine" : "environmentFile", data.GetProperty("winner").GetString());
        Assert.False(data.TryGetProperty("catalogOnly", out _));
        Assert.False(data.TryGetProperty("baseOnly", out _));
        Assert.Equal(!commandLineOverride, data.TryGetProperty("items", out _));
        if (commandLineOverride)
            Assert.False(data.GetProperty("enabled").GetBoolean());
        else
            Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal("Testing", app.Environment.EnvironmentName);
        Assert.Equal(files.Options().ContentRootPath, app.Environment.ContentRootPath);
        Assert.Equal("test-pipeline-index", app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").RulesIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EnvironmentAndCommandLineReplaceCompleteExtraDataObject(bool commandLineOverride)
    {
        const string key = "PipelineCatalog__Pipelines__test__ExtraData";
        var previous = Environment.GetEnvironmentVariable(key);
        try
        {
            Environment.SetEnvironmentVariable(key, """{"winner":"environment","Name":"upper","name":"lower","items":[]}""");
            using var files = new RuntimeFiles();
            files.Write("pipelinecatalog.json", Configuration("""{"winner":"catalog","catalogOnly":true}"""));
            files.Write("appsettings.Testing.json", ExtraDataOverride("""{"winner":"environmentFile","fileOnly":true}"""));
            string[] args = commandLineOverride
                ? ["--PipelineCatalog:Pipelines:test:ExtraData", """{"winner":"commandLine","number":12}"""]
                : [];
            var builder = GatewayApplicationBuilder.Create(["--PipelineCatalogFile", files.CatalogPath, .. args], files.Options());
            RegisterCatalog(builder);
            await using var app = builder.Build();
            var data = app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").ExtraData.Value;

            Assert.Equal(commandLineOverride ? "commandLine" : "environment", data.GetProperty("winner").GetString());
            Assert.False(data.TryGetProperty("catalogOnly", out _));
            Assert.False(data.TryGetProperty("fileOnly", out _));
            if (commandLineOverride)
            {
                Assert.Equal(12, data.GetProperty("number").GetInt32());
                Assert.False(data.TryGetProperty("Name", out _));
            }
            else
            {
                Assert.Equal("upper", data.GetProperty("Name").GetString());
                Assert.Equal("lower", data.GetProperty("name").GetString());
                Assert.Empty(data.GetProperty("items").EnumerateArray());
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, previous);
        }
    }

    [Fact]
    public async Task SelectedCatalogReplacesBundledPipelinesAndResolvesRelativeToContentRoot()
    {
        using var files = new RuntimeFiles();
        files.Write("deployment.json", Configuration("""{"flag":true,"items":[1,null],"Name":"upper","name":"lower"}"""));
        var builder = GatewayApplicationBuilder.Create(
            ["--PipelineCatalogFile", "deployment.json", "--PipelineCatalog:Pipelines:test:RulesIndex", "custom-integ-index"], files.Options());
        RegisterCatalog(builder);
        await using var app = builder.Build();

        var pipeline = Assert.Single(app.Services.GetRequiredService<IPipelineCatalog>().GetAll());
        Assert.Equal("test", pipeline.PipelineId);
        Assert.Equal("custom-integ-index", pipeline.RulesIndex);
        Assert.True(pipeline.ExtraData.Value.GetProperty("flag").GetBoolean());
        Assert.Equal("upper", pipeline.ExtraData.Value.GetProperty("Name").GetString());
        Assert.Equal("lower", pipeline.ExtraData.Value.GetProperty("name").GetString());
    }

    [Fact]
    public async Task ExplicitOptionsAndOptionArgumentsRetainStandardHostSemantics()
    {
        using var files = new RuntimeFiles();
        files.Write("pipelinecatalog.json", Configuration("{}"));
        files.Write("appsettings.Testing.json", """{"HostSetting":"testing"}""");
        var options = files.Options(["--PipelineCatalogFile", files.CatalogPath, "--environment", "CommandLineEnvironment"]);
        var builder = GatewayApplicationBuilder.Create(["--PipelineCatalogFile", "ignored.json"], options);
        RegisterCatalog(builder);
        await using var app = builder.Build();

        Assert.Equal("Testing", app.Environment.EnvironmentName);
        Assert.Equal(options.ContentRootPath, app.Environment.ContentRootPath);
        Assert.Equal(options.ApplicationName, app.Environment.ApplicationName);
        Assert.Equal("testing", builder.Configuration["HostSetting"]);
        Assert.Single(app.Services.GetRequiredService<IPipelineCatalog>().GetAll());
    }

    [Fact]
    public void MissingSelectedCatalogFailsInsteadOfFallingBackToSample()
    {
        using var files = new RuntimeFiles();
        Assert.Throws<FileNotFoundException>(() => GatewayApplicationBuilder.Create(
            ["--PipelineCatalogFile", "missing.json"], files.Options()));
    }

    private static void RegisterCatalog(WebApplicationBuilder builder)
    {
        builder.Services.AddSingleton<IPipelineContract, AsdPipelineContract>();
        builder.Services.AddSingleton<IPipelineContractRegistry, PipelineContractRegistry>();
        builder.Services.AddPipelineCatalog(builder.Configuration);
    }

    private static string Configuration(string extraData) => $$"""
        {
          "PipelineCatalog": {
            "Pipelines": {
              "test": {
                "Enabled": true,
                "ContractId": "asd",
                "RulesIndex": "test-pipeline-index",
                "ExtraData": {{extraData}},
                "Transport": { "Kind": "http", "Http": { "Endpoint": "https://example.invalid/work" } }
              }
            }
          }
        }
        """;

    private static string ExtraDataOverride(string extraData) => $$"""
        {
          "PipelineCatalog": {
            "Pipelines": {
              "test": {
                "ExtraData": {{JsonSerializer.Serialize(extraData)}}
              }
            }
          }
        }
        """;

    private sealed class RuntimeFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"unified-gateway-config-{Guid.NewGuid():N}");
        private readonly HashSet<string> _files = [];

        public RuntimeFiles()
        {
            Directory.CreateDirectory(_directory);
            Write("pipelinecatalog.json", "{}");
        }

        public string CatalogPath => Path.Combine(_directory, "pipelinecatalog.json");

        public WebApplicationOptions Options(string[]? args = null) => new()
        {
            Args = args,
            ContentRootPath = _directory,
            EnvironmentName = "Testing",
            ApplicationName = typeof(Program).Assembly.GetName().Name
        };

        public void Write(string name, string contents)
        {
            var path = Path.Combine(_directory, name);
            File.WriteAllText(path, contents);
            _files.Add(path);
        }

        public void Dispose()
        {
            foreach (var file in _files)
                File.Delete(file);
            Directory.Delete(_directory);
        }
    }
}

[CollectionDefinition("Gateway configuration environment", DisableParallelization = true)]
public sealed class GatewayConfigurationEnvironmentCollection { }
