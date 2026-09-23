using System.Text;
using System.Text.Json;
using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class PipelineExtraDataConfigurationTests
{
    private const string ExtraDataKey = "PipelineCatalog:Pipelines:0:ExtraData";

    [Fact]
    public async Task JsonStreamPreservesArbitraryExtraDataTypesThroughCatalogStartup()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration("""
                {
                  "numericString": "123",
                  "number": 123,
                  "decimal": 1.25,
                  "largeNumber": 123456789012345678901234567890,
                  "booleanString": "true",
                  "boolean": true,
                  "falseValue": false,
                  "nothing": null,
                  "emptyObject": {},
                  "emptyArray": [],
                  "array": ["7", 7, false, null, {}, [], { "nested": [1, "1"] }],
                  "nested": { "Name": "upper", "name": "lower", "key:with:colons": "value" }
                }
                """)))
            .Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var data = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").ExtraData.Value;

        Assert.Equal("123", data.GetProperty("numericString").GetString());
        Assert.Equal(123, data.GetProperty("number").GetInt32());
        Assert.Equal(1.25m, data.GetProperty("decimal").GetDecimal());
        Assert.Equal("123456789012345678901234567890", data.GetProperty("largeNumber").GetRawText());
        Assert.Equal("true", data.GetProperty("booleanString").GetString());
        Assert.True(data.GetProperty("boolean").GetBoolean());
        Assert.False(data.GetProperty("falseValue").GetBoolean());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nothing").ValueKind);
        Assert.Empty(data.GetProperty("emptyObject").EnumerateObject());
        Assert.Equal(0, data.GetProperty("emptyArray").GetArrayLength());
        var items = data.GetProperty("array");
        Assert.Equal("7", items[0].GetString());
        Assert.Equal(7, items[1].GetInt32());
        Assert.False(items[2].GetBoolean());
        Assert.Equal(JsonValueKind.Null, items[3].ValueKind);
        Assert.Equal(JsonValueKind.Object, items[4].ValueKind);
        Assert.Equal(JsonValueKind.Array, items[5].ValueKind);
        Assert.Equal(1, items[6].GetProperty("nested")[0].GetInt32());
        Assert.Equal("1", items[6].GetProperty("nested")[1].GetString());
        Assert.Equal("upper", data.GetProperty("nested").GetProperty("Name").GetString());
        Assert.Equal("lower", data.GetProperty("nested").GetProperty("name").GetString());
        Assert.Equal("value", data.GetProperty("nested").GetProperty("key:with:colons").GetString());
        Assert.Empty(configuration.GetSection(ExtraDataKey).GetChildren());
        await host.StopAsync();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{}")]
    public async Task MissingOrEmptyExtraDataBindsAsEmptyObject(string? extraData)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration(extraData)))
            .Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var data = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").ExtraData.Value;

        Assert.Equal(JsonValueKind.Object, data.ValueKind);
        Assert.Empty(data.EnumerateObject());
        await host.StopAsync();
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"text\"")]
    [InlineData("\"{\\\"value\\\":true}\"")]
    public void JsonFileStyleExtraDataMustBeAnObject(string extraData)
    {
        var builder = new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration(extraData)));

        var exception = Assert.Throws<FormatException>(() => builder.Build());

        Assert.Contains(ExtraDataKey, exception.Message, StringComparison.Ordinal);
        Assert.Contains("JSON object", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CommentsAndTrailingCommasRemainSupportedInsideExtraData()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration("""
                {
                  // Values retain their JSON types.
                  "enabled": true,
                  "items": ["one",],
                  "nested": { "value": null, },
                }
                """)))
            .Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var data = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").ExtraData.Value;

        Assert.True(data.GetProperty("enabled").GetBoolean());
        Assert.Equal("one", Assert.Single(data.GetProperty("items").EnumerateArray()).GetString());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nested").GetProperty("value").ValueKind);
        await host.StopAsync();
    }

    [Fact]
    public async Task LaterExactKeyJsonOverrideReplacesWholeExtraDataObject()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration("""{"oldOnly": true, "winner": "file"}""")))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ExtraDataKey] = """{"winner":"environment","enabled":false,"numericString":"123"}"""
            })
            .Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();
        var data = host.Services.GetRequiredService<IPipelineCatalog>().GetRequired("asd").ExtraData.Value;

        Assert.Equal("environment", data.GetProperty("winner").GetString());
        Assert.False(data.TryGetProperty("oldOnly", out _));
        Assert.False(data.GetProperty("enabled").GetBoolean());
        Assert.Equal("123", data.GetProperty("numericString").GetString());
        await host.StopAsync();
    }

    [Fact]
    public async Task LaterJsonSourceOverridesEarlierExactKeyValue()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ExtraDataKey] = """{"winner":"earlier"}""" })
            .AddPipelineCatalogJsonStream(JsonStream(Configuration("""{"winner":"later"}""")))
            .Build();
        using var host = CreateHost(configuration);

        await host.StartAsync();

        Assert.Equal("later", host.Services.GetRequiredService<IPipelineCatalog>()
            .GetRequired("asd").ExtraData.Value.GetProperty("winner").GetString());
        await host.StopAsync();
    }

    [Fact]
    public void FileSourcesKeepSettingsOrderAndSupportReload()
    {
        using var files = new TemporaryJsonFiles();
        files.Write("appsettings.json", Configuration("""{"winner":"base"}"""));
        files.Write("appsettings.Integration.json", Configuration("""{"winner":"environment"}"""));
        using var fileProvider = new PhysicalFileProvider(files.Path);
        var builder = new ConfigurationBuilder()
            .AddJsonFile(fileProvider, "appsettings.json", optional: false, reloadOnChange: true)
            .AddJsonFile(fileProvider, "missing.json", optional: true, reloadOnChange: false)
            .AddJsonFile(fileProvider, "appsettings.Integration.json", optional: false, reloadOnChange: false);
        var originalSources = builder.Sources.Cast<JsonConfigurationSource>().ToArray();
        originalSources[0].ReloadDelay = 17;
        builder.PreservePipelineExtraDataJson();
        var wrappedSources = builder.Sources.ToArray();
        builder.PreservePipelineExtraDataJson();
        using var configuration = (ConfigurationRoot)builder.Build();

        Assert.Equal(wrappedSources, builder.Sources);
        var providers = configuration.Providers.Cast<JsonConfigurationProvider>().ToArray();
        for (var index = 0; index < originalSources.Length; index++)
        {
            Assert.Same(originalSources[index], providers[index].Source);
        }

        using (var data = JsonDocument.Parse(configuration[ExtraDataKey]!))
        {
            Assert.Equal("environment", data.RootElement.GetProperty("winner").GetString());
        }

        files.Write("appsettings.Integration.json", Configuration("""{"winner":"reloaded","items":[]}"""));
        configuration.Reload();
        using var reloaded = JsonDocument.Parse(configuration[ExtraDataKey]!);
        Assert.Equal("reloaded", reloaded.RootElement.GetProperty("winner").GetString());
        Assert.Equal(0, reloaded.RootElement.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task WrappingLiveConfigurationManagerPreservesHigherPriorityProviders()
    {
        using var files = new TemporaryJsonFiles();
        files.Write("appsettings.json", Configuration("""{"winner":"file"}"""));
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(System.IO.Path.Combine(files.Path, "appsettings.json"), optional: false);
        configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ExtraDataKey] = """{"winner":"environment","items":[]}"""
        });

        configuration.PreservePipelineExtraDataJson();
        using var host = CreateHost(configuration);
        await host.StartAsync();

        Assert.Equal("environment", host.Services.GetRequiredService<IPipelineCatalog>()
            .GetRequired("asd").ExtraData.Value.GetProperty("winner").GetString());
        await host.StopAsync();
    }

    [Fact]
    public void WrappingLiveFileSourcesAfterAStreamWasConsumedFailsClearlyBeforeSourceMutation()
    {
        using var files = new TemporaryJsonFiles();
        files.Write("appsettings.json", Configuration());
        using var configuration = new ConfigurationManager();
        configuration.AddJsonFile(System.IO.Path.Combine(files.Path, "appsettings.json"), optional: false);
        configuration.AddPipelineCatalogJsonStream(JsonStream("{}"));
        var sources = configuration.Sources.ToArray();

        var exception = Assert.Throws<InvalidOperationException>(() => configuration.PreservePipelineExtraDataJson());

        Assert.Contains("before adding stream", exception.Message, StringComparison.Ordinal);
        Assert.Equal(sources, configuration.Sources);
    }

    [Fact]
    public async Task UnknownPipelineSiblingStillFailsStrictStartupBinding()
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration("{}", ", \"UnexpectedSetting\": true")))
            .Build();
        using var host = CreateHost(configuration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("UnexpectedSetting", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("true")]
    [InlineData("not JSON")]
    public async Task InvalidExactKeyJsonOverrideFailsHostStartup(string value)
    {
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder()
            .AddPipelineCatalogJsonStream(JsonStream(Configuration()))
            .AddInMemoryCollection(new Dictionary<string, string?> { [ExtraDataKey] = value })
            .Build();
        using var host = CreateHost(configuration);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync());

        Assert.Contains("ExtraData", exception.ToString(), StringComparison.Ordinal);
    }

    private static IHost CreateHost(IConfiguration configuration)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddConfiguration(configuration);
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IPipelineContractRegistry>(new PipelineContractRegistry([new AsdPipelineContract()]));
        builder.Services.AddPipelineCatalog(builder.Configuration);
        return builder.Build();
    }

    private static MemoryStream JsonStream(string value) => new(Encoding.UTF8.GetBytes(value));

    private static string Configuration(string? extraData = "{}", string extraProperty = "") => $$"""
        {
          "PipelineCatalog": {
            "Pipelines": [{
              "PipelineId": "asd",
              "ContractId": "asd",
              "Enabled": true,
              "RulesIndex": "rules-integ",
              "Transport": {
                "Kind": "http",
                "Http": { "Endpoint": "https://service.example/process" }
              }
              {{(extraData is null ? string.Empty : $", \"ExtraData\": {extraData}")}}
              {{extraProperty}}
            }]
          }
        }
        """;

    private sealed class TemporaryJsonFiles : IDisposable
    {
        private readonly List<string> _files = [];
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"pipeline-extra-data-{Guid.NewGuid():N}");

        public TemporaryJsonFiles() => Directory.CreateDirectory(Path);

        public void Write(string name, string value)
        {
            var path = System.IO.Path.Combine(Path, name);
            File.WriteAllText(path, value);
            if (!_files.Contains(path))
            {
                _files.Add(path);
            }
        }

        public void Dispose()
        {
            foreach (var file in _files)
            {
                File.Delete(file);
            }

            Directory.Delete(Path);
        }
    }
}
