using System.Text.Json;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.UnifiedGateway.Processing;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Catalog = ImagingPipeline.PipelineCatalog.PipelineCatalog;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class AlgoPreparationTests
{
    [Fact]
    public void TwoAlgoPipelinesBuildFromTheSameOverlayWithIndependentSettingsAndDestinations()
    {
        var preparer = CreatePreparer();
        var context = OverlayDispatchContextFactory.Create("task-a", "rule-a", Json("""
            {"id":"image-a","photoTime":"2026-09-23T10:00:00","legId":"leg-a",
             "prevOverlayId":null,"nextOverlayId":"image-b"}
            """));
        var first = preparer.Prepare("algo-first", context, RuleParameters());
        var second = preparer.Prepare("algo-second", context, RuleParameters());

        Assert.Equal(PipelinePreparationStatus.Prepared, first.Status);
        Assert.Equal(PipelinePreparationStatus.Prepared, second.Status);
        using var firstBody = JsonDocument.Parse(first.Work!.Payload.Body);
        using var secondBody = JsonDocument.Parse(second.Work!.Payload.Body);
        Assert.Equal("first-origin", firstBody.RootElement.GetProperty("origin").GetString());
        Assert.Equal("second-origin", secondBody.RootElement.GetProperty("origin").GetString());
        Assert.True(firstBody.RootElement.GetProperty("saveDetections").GetBoolean());
        Assert.False(secondBody.RootElement.GetProperty("saveDetections").GetBoolean());
        Assert.Equal("first-user", firstBody.RootElement.GetProperty("username").GetString());
        Assert.Equal("second-user", secondBody.RootElement.GetProperty("username").GetString());
        Assert.Equal("example-algorithm", firstBody.RootElement.GetProperty("algorithmName").GetString());
        Assert.Equal("image-a", firstBody.RootElement.GetProperty("tasksData")[0].GetProperty("imageId").GetString());
        Assert.False(firstBody.RootElement.TryGetProperty("extraData", out _));
        Assert.NotEqual(first.Work.Pipeline.RulesIndex, second.Work.Pipeline.RulesIndex);
        Assert.NotEqual(first.Work.Pipeline.Transport.Http!.Endpoint, second.Work.Pipeline.Transport.Http!.Endpoint);
    }

    [Fact]
    public void InvalidAlgoContextReturnsFieldErrorsWithoutBuildingWork()
    {
        var context = OverlayDispatchContextFactory.Create("task", "rule", Json("""
            {"id":"image-a","photoTime":"2026-09-23T10:00:00"}
            """)) with { ImageId = "" };
        var result = CreatePreparer().Prepare("algo-first", context, RuleParameters());
        Assert.Equal(PipelinePreparationStatus.Invalid, result.Status);
        Assert.Null(result.Work);
        Assert.NotEmpty(result.Errors);
    }

    private static PipelineWorkPreparer CreatePreparer()
    {
        var registry = new PipelineContractRegistry([new AlgoPipelineContract()]);
        var catalog = new Catalog(Options.Create(new PipelineCatalogOptions
        {
            Pipelines = new(StringComparer.Ordinal)
            {
                ["algo-first"] = Definition("first", true),
                ["algo-second"] = Definition("second", false)
            }
        }), registry, NullLogger<Catalog>.Instance);
        return new(catalog, registry, NullLogger<PipelineWorkPreparer>.Instance);
    }

    private static PipelineDefinition Definition(string name, bool save) => new()
    {
        PipelineId = $"algo-{name}", ContractId = "algo", Enabled = true,
        RulesIndex = $"algo-{name}-integ-pipeline-index",
        ExtraData = new PipelineExtraData(JsonSerializer.SerializeToElement(new
        {
            XUserName = $"{name}-user", Origin = $"{name}-origin", QueueType = $"{name}-queue", SaveDetections = save
        })),
        Transport = new() { Kind = "http", Http = new() { Method = "PUT", Endpoint = $"https://{name}.example.invalid/mission/upsert/" } }
    };

    private static JsonElement RuleParameters() => Json("""
        {"customer":"example-customer","profile_name":"example-profile","hebrew_rule_name":"Example",
         "algorithm_name":"example-algorithm","priority":1,"username":null,
         "run_every_other_image":true,"should_check_in_vip":true,
         "location_geojson":{"type":"Polygon","coordinates":[[[0,0],[2,0],[2,2],[0,0]]]}}
        """);

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
