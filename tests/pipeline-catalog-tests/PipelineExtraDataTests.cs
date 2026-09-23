using System.Text.Json;
using ImagingPipeline.PipelineContracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Catalog = ImagingPipeline.PipelineCatalog.PipelineCatalog;

namespace ImagingPipeline.PipelineCatalog.Tests;

public sealed class PipelineExtraDataTests
{
    [Fact]
    public void SnapshotOwnsJsonBeyondDocumentLifetimeAndOptionsReplacement()
    {
        PipelineExtraData extra;
        using (var document = JsonDocument.Parse("""{"text":"true","flag":true,"empty":[],"object":{}}"""))
            extra = new(document.RootElement);
        var options = OptionsWith(extra);
        var catalog = new Catalog(Options.Create(options), new PipelineContractRegistry([new AsdPipelineContract()]), NullLogger<Catalog>.Instance);

        options.Pipelines[0] = options.Pipelines[0] with { ExtraData = PipelineExtraData.Parse("{\"changed\":true}") };
        var data = catalog.GetRequired("asd").ExtraData.Value;

        Assert.Equal(JsonValueKind.String, data.GetProperty("text").ValueKind);
        Assert.Equal(JsonValueKind.True, data.GetProperty("flag").ValueKind);
        Assert.Equal(JsonValueKind.Array, data.GetProperty("empty").ValueKind);
        Assert.Equal(JsonValueKind.Object, data.GetProperty("object").ValueKind);
        Assert.False(data.TryGetProperty("changed", out _));
    }

    [Fact]
    public void ExtraDataStringRepresentationDoesNotPrintValues()
    {
        var extra = PipelineExtraData.Parse("{\"sensitive\":\"private-data\"}");
        Assert.DoesNotContain("private-data", extra.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NullProgrammaticExtraDataFailsCatalogValidation()
    {
        var options = OptionsWith(null!);
        var registry = new PipelineContractRegistry([new AsdPipelineContract()]);
        var error = Assert.Throws<OptionsValidationException>(() => new Catalog(Options.Create(options), registry, NullLogger<Catalog>.Instance));
        Assert.Contains("ExtraData", error.Message, StringComparison.Ordinal);
    }

    private static PipelineCatalogOptions OptionsWith(PipelineExtraData extra) => new()
    {
        Pipelines = [new()
        {
            PipelineId = "asd", ContractId = "asd", Enabled = true, RulesIndex = "asd-integ-pipeline-index",
            ExtraData = extra, Transport = new() { Kind = "http", Http = new() { Endpoint = "https://example.invalid/" } }
        }]
    };
}
