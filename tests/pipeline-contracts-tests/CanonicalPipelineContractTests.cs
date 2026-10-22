using System.Text.Json;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class CanonicalPipelineContractTests
{
    [Theory]
    [InlineData("FindAir")]
    [InlineData("DeepWisdom")]
    [InlineData("SarAtc")]
    public void ExtraDataAcceptsArbitraryNamesAndPreservesEveryJsonType(string contractId)
    {
        var contract = Contract(contractId);
        var extraData = Json("""
            {
              "Name":"upper","name":"lower","key:part":{"label":"שלום 🙂 漢字"},
              "nested":[null,true,false,{},[],{"large":9007199254740993}],
              "decimal":0.12345678901234567890123456789,"optional":null,
              "Origin":{"arbitrary":true},"QueueType":42,"SaveDetections":"as supplied",
              "XUserName":["custom","value"],"tasksData":{"doNotOverride":true}
            }
            """);

        Assert.Empty(contract.ValidateExtraData(extraData));
        var payload = contract.BuildPayload(Context(), RunParams(contractId), extraData);

        using var document = JsonDocument.Parse(payload.Body);
        var written = document.RootElement.GetProperty("extraData");
        Assert.True(JsonElement.DeepEquals(extraData, written));
        Assert.Equal(extraData.GetProperty("decimal").GetRawText(), written.GetProperty("decimal").GetRawText());
        Assert.Equal("שלום 🙂 漢字", written.GetProperty("key:part").GetProperty("label").GetString());
        Assert.Equal("upper", written.GetProperty("Name").GetString());
        Assert.Equal("lower", written.GetProperty("name").GetString());
        Assert.DoesNotContain("key:part", payload.Attributes.Keys);
        if (contractId == "DeepWisdom")
            Assert.Equal(JsonValueKind.Array, document.RootElement.GetProperty("tasksData").ValueKind);
    }

    [Theory]
    [InlineData("FindAir")]
    [InlineData("DeepWisdom")]
    [InlineData("SarAtc")]
    public void MissingAndEmptyExtraDataNeedNoNamedSettingsAndAreOmitted(string contractId)
    {
        var contract = Contract(contractId);
        Assert.Empty(contract.ValidateExtraData(default));
        Assert.Empty(contract.ValidateExtraData(Json("{}")));

        var omitted = contract.BuildPayload(Context(), RunParams(contractId));
        var empty = contract.BuildPayload(Context(), RunParams(contractId), Json("{}"));

        Assert.Equal(omitted.Body, empty.Body);
        using var body = JsonDocument.Parse(omitted.Body);
        Assert.False(body.RootElement.TryGetProperty("extraData", out _));
        if (contractId == "DeepWisdom")
        {
            Assert.False(body.RootElement.TryGetProperty("origin", out _));
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("username").ValueKind);
        }
    }

    [Fact]
    public void DeepWisdomRetainsConfiguredTopLevelMappingsWithoutImposingTypes()
    {
        var extraData = Json("""
            {"XUserName":["operator"],"Origin":{"region":7},"QueueType":null,
             "SaveDetections":"arbitrary-value","additional":"kept"}
            """);

        var payload = new DeepWisdomPipelineContract().BuildPayload(Context(), RunParams("DeepWisdom"), extraData);

        using var body = JsonDocument.Parse(payload.Body);
        foreach (var pair in new[]
                 {
                     ("XUserName", "username"), ("Origin", "origin"),
                     ("QueueType", "queueType"), ("SaveDetections", "saveDetections")
                 })
            Assert.True(JsonElement.DeepEquals(extraData.GetProperty(pair.Item1), body.RootElement.GetProperty(pair.Item2)));
        Assert.True(JsonElement.DeepEquals(extraData, body.RootElement.GetProperty("extraData")));
    }

    [Theory]
    [InlineData("FindAir", "null")]
    [InlineData("FindAir", "[]")]
    [InlineData("DeepWisdom", "null")]
    [InlineData("DeepWisdom", "42")]
    [InlineData("SarAtc", "[]")]
    public void ExtraDataContainerMustStillBeAnObject(string contractId, string json)
    {
        var contract = Contract(contractId);
        var extraData = Json(json);

        Assert.Contains(contract.ValidateExtraData(extraData), error => error.Field == "extraData");
        Assert.Throws<ArgumentException>(() => contract.BuildPayload(Context(), RunParams(contractId), extraData));
    }

    [Fact]
    public void CanonicalContractsUseOnlyFindAirAndDeepWisdomIds()
    {
        var registry = new PipelineContractRegistry([new FindAirPipelineContract(), new DeepWisdomPipelineContract()]);
        Assert.IsType<FindAirPipelineContract>(registry.GetRequired("FindAir"));
        Assert.IsType<DeepWisdomPipelineContract>(registry.GetRequired("DeepWisdom"));
        Assert.False(registry.TryGet("asd", out _));
        Assert.False(registry.TryGet("algo", out _));
    }

    private static IPipelineContract Contract(string id) => id switch
    {
        "FindAir" => new FindAirPipelineContract(),
        "SarAtc" => new SarAtcPipelineContract(),
        _ => new DeepWisdomPipelineContract()
    };

    private static JsonElement RunParams(string id) => Json(id switch
    {
        "FindAir" => """
            {"tenantId":"tenant","algorithmNames":["FindAir"],
             "tilingConfigs":[{"tileSizeWidth":512,"tileSizeHeight":512,"tileOverlapWidth":0,"tileOverlapHeight":0}]}
            """,
        "SarAtc" => """{"modelName":"model","fieldsToFilter":[]}""",
        _ => """
            {"customer":"customer","modelName":"profile","hebrewRuleName":"משימה",
             "algorithmName":"custom","priority":1,"username":null,
             "runEveryOtherImage":false,"shouldCheckInVip":false}
            """
    });

    private static PipelineDispatchContext Context() => new(
        "task", "rule", "image", Json("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}"""),
        new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), "EO", "/image.tiff", 100, 100,
        0.7, "camera", null, "grid", "/grid.json")
    {
        RuleLocationWkt = "POLYGON ((0 0, 1 0, 1 1, 0 0))"
    };

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
