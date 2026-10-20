using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;

namespace ImagingPipeline.PipelineContracts.Tests;

public sealed class SarAtcPipelineContractTests
{
    private const string ValidRunParams = """
        {"modelName":"sar-atc-v2","frameMetadata":{"band":"X","angles":[12.5,30],"nested":{"מסגרת":null}}}
        """;

    private readonly SarAtcPipelineContract _contract = new();

    [Fact]
    public void BuildPayloadWritesTheSarAtcWireBodyAndVersionHeader()
    {
        const string expected = """
            {"imageId":"image-1","modelName":"sar-atc-v2","frameMetadata":{"band":"X","angles":[12.5,30],"nested":{"מסגרת":null}},"requestId":"dispatch-1"}
            """;

        var payload = _contract.BuildPayload(Context(), Json(ValidRunParams));

        Assert.Equal(expected, Encoding.UTF8.GetString(payload.Body));
        Assert.Equal("application/json", payload.ContentType);
        Assert.Empty(payload.Attributes);
        var header = Assert.Single(payload.RabbitMqAttributes!);
        Assert.Equal("sar-atc-contract-version", header.Key);
        Assert.Equal(1, Assert.IsType<int>(header.Value));
    }

    [Fact]
    public void FrameMetadataIsPassedThroughUnchanged()
    {
        var runParams = Json("""{"modelName":"m","frameMetadata":{"large":9007199254740993,"decimal":0.12345678901234567890}}""");

        var payload = _contract.BuildPayload(Context(), runParams);

        using var body = JsonDocument.Parse(payload.Body);
        var written = body.RootElement.GetProperty("frameMetadata");
        Assert.True(JsonElement.DeepEquals(runParams.GetProperty("frameMetadata"), written));
        Assert.Equal("0.12345678901234567890", written.GetProperty("decimal").GetRawText());
    }

    [Fact]
    public void EmptyFrameMetadataIsAllowed()
    {
        Assert.Empty(_contract.ValidateRunParams(Json("""{"modelName":"m","frameMetadata":{}}""")));
    }

    [Theory]
    [InlineData("""{"frameMetadata":{}}""", "modelName")]
    [InlineData("""{"modelName":" ","frameMetadata":{}}""", "modelName")]
    [InlineData("""{"modelName":7,"frameMetadata":{}}""", "modelName")]
    [InlineData("""{"modelName":"m"}""", "frameMetadata")]
    [InlineData("""{"modelName":"m","frameMetadata":[]}""", "frameMetadata")]
    [InlineData("""{"modelName":"m","frameMetadata":null}""", "frameMetadata")]
    [InlineData("""{"modelName":"m","frameMetadata":{},"modelname":"typo"}""", "modelname")]
    [InlineData("""{"modelName":"m","modelName":"n","frameMetadata":{}}""", "modelName")]
    [InlineData("""[]""", "runParams")]
    public void InvalidRunParamsAreRejectedWithTheirField(string runParams, string field)
    {
        var errors = _contract.ValidateRunParams(Json(runParams));

        Assert.Contains(errors, error => error.Field == field);
        Assert.Throws<ArgumentException>(() => _contract.BuildPayload(Context(), Json(runParams)));
    }

    [Fact]
    public void RuleLocationIsOptional()
    {
        IPipelineContract contract = _contract;

        Assert.Empty(contract.ValidateRuleLocation(null));
        Assert.Empty(contract.ValidateRuleLocation("POLYGON ((0 0, 1 0, 1 1, 0 0))"));
    }

    [Fact]
    public void ContextNeedsTheImageAndDispatchIds()
    {
        var errors = _contract.ValidateContext(Context() with { TaskId = "", ImageId = " " });

        Assert.Equal(["taskId", "input.id"], errors.Select(error => error.Field));
        Assert.Empty(_contract.ValidateContext(Context() with { ImageUrl = "", GridUri = "", SensorType = "" }));
    }

    [Fact]
    public void ContractIsRegisteredForEveryHost()
    {
        var services = new ServiceCollection().AddPipelineContracts();

        Assert.Contains(services, service =>
            service.ServiceType == typeof(IPipelineContract) && service.ImplementationType == typeof(SarAtcPipelineContract));
    }

    private static PipelineDispatchContext Context() => new(
        "dispatch-1", "rule-1", "image-1", Json("""{"type":"Polygon","coordinates":[[[0,0],[1,0],[1,1],[0,0]]]}"""),
        new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), "SAR", "/image.tiff", 100, 100,
        0.7, "camera", null, "grid", "/grid.json");

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
