using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class GatewayApplicationBuilderTests
{
    [Fact]
    public async Task FirstAppSettingsLoadPreservesCaseDistinctAndColonKeysInExtraData()
    {
        using var files = new RuntimeFiles();
        files.Write("appsettings.json", Configuration("""
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
        var builder = GatewayApplicationBuilder.Create([], files.Options());
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
    public async Task EnvironmentFileAndCommandLineKeepTheirOverridePrecedence(bool commandLineOverride)
    {
        using var files = new RuntimeFiles();
        files.Write("appsettings.json", Configuration("""{"winner":"base","baseOnly":true}"""));
        files.Write("appsettings.Testing.json", """
            { "PipelineCatalog": { "Pipelines": [{ "ExtraData": { "winner": "environment", "items": [] } }] } }
            """);
        string[] args = commandLineOverride
            ? ["--PipelineCatalog:Pipelines:0:ExtraData", "{\"winner\":\"commandLine\",\"enabled\":false}"]
            : [];
        var builder = GatewayApplicationBuilder.Create(args, files.Options());
        RegisterCatalog(builder);
        await using var app = builder.Build();

        var data = app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").ExtraData.Value;

        Assert.Equal(commandLineOverride ? "commandLine" : "environment", data.GetProperty("winner").GetString());
        Assert.False(data.TryGetProperty("baseOnly", out _));
        Assert.Equal(!commandLineOverride, data.TryGetProperty("items", out _));
        if (commandLineOverride)
            Assert.False(data.GetProperty("enabled").GetBoolean());
        else
            Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal("Testing", app.Environment.EnvironmentName);
        Assert.Equal("test-pipeline-index", app.Services.GetRequiredService<IPipelineCatalog>().GetRequired("test").RulesIndex);
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
            "Pipelines": [{
              "PipelineId": "test",
              "Enabled": true,
              "ContractId": "asd",
              "RulesIndex": "test-pipeline-index",
              "ExtraData": {{extraData}},
              "Transport": { "Kind": "http", "Http": { "Endpoint": "https://example.invalid/work" } }
            }]
          }
        }
        """;

    private sealed class RuntimeFiles : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), $"unified-gateway-config-{Guid.NewGuid():N}");
        private readonly List<string> _files = [];

        public RuntimeFiles() => Directory.CreateDirectory(_directory);

        public WebApplicationOptions Options() => new()
        {
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
