using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class PipelineCatalogFileConfigurationTests
{
    [Fact]
    public void ReusableLoaderPreservesContractSettingsOnFirstLiveLoad()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"catalog-file-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "catalog.json");
        try
        {
            File.WriteAllText(file, """
                {"PipelineCatalog":{"Pipelines":{"asd":{"ExtraData":{
                  "Name":"upper","name":"lower","key:part":true,"key":{"part":false},
                  "large":9007199254740993,"items":[null,{},[]]
                }}}}}
                """);
            using var configuration = new ConfigurationManager();
            configuration.SetBasePath(directory);
            configuration.AddPipelineCatalogJsonFile("catalog.json");
            using var document = JsonDocument.Parse(configuration["PipelineCatalog:Pipelines:asd:ExtraData"]!);
            var data = document.RootElement;
            Assert.Equal("upper", data.GetProperty("Name").GetString());
            Assert.Equal("lower", data.GetProperty("name").GetString());
            Assert.True(data.GetProperty("key:part").GetBoolean());
            Assert.False(data.GetProperty("key").GetProperty("part").GetBoolean());
            Assert.Equal(9007199254740993L, data.GetProperty("large").GetInt64());
            Assert.Equal(JsonValueKind.Null, data.GetProperty("items")[0].ValueKind);
            Assert.Equal(JsonValueKind.Object, data.GetProperty("items")[1].ValueKind);
            Assert.Equal(JsonValueKind.Array, data.GetProperty("items")[2].ValueKind);

            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PipelineCatalog:Pipelines:asd:ExtraData"] = "{\"replacement\":false}"
            });
            using var replaced = JsonDocument.Parse(configuration["PipelineCatalog:Pipelines:asd:ExtraData"]!);
            Assert.Single(replaced.RootElement.EnumerateObject());
            Assert.False(replaced.RootElement.GetProperty("replacement").GetBoolean());
        }
        finally
        {
            File.Delete(file);
            Directory.Delete(directory);
        }
    }
}
