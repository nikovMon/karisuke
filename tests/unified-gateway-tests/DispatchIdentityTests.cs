using System.Text.Json;
using ImagingPipeline.UnifiedGateway.Dispatch;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class DispatchIdentityTests
{
    [Fact]
    public void IdContainsImagePipelineRuleAndRunParamsHash()
    {
        var id = DispatchIdentity.Create("image-a", "asd", "rule-a", Parse("""{"tenantId":"t"}"""));

        var parts = id.Split(':');
        Assert.Equal(["image-a", "asd", "rule-a"], parts[..3]);
        Assert.Matches("^[0-9a-f]{16}$", parts[3]);
    }

    [Fact]
    public void IdIsStableAcrossPropertyOrderAndWhitespace()
    {
        var first = DispatchIdentity.Create("image-a", "asd", "rule-a",
            Parse("""{"tenantId":"t","tilingConfigs":[{"w":1,"h":2}],"algorithmNames":["FindAir"]}"""));
        var second = DispatchIdentity.Create("image-a", "asd", "rule-a",
            Parse("""
                  {
                    "algorithmNames": [ "FindAir" ],
                    "tilingConfigs": [ { "h": 2, "w": 1 } ],
                    "tenantId": "t"
                  }
                  """));

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("image-b", "asd", "rule-a", """{"tenantId":"t"}""")]
    [InlineData("image-a", "algo", "rule-a", """{"tenantId":"t"}""")]
    [InlineData("image-a", "asd", "rule-b", """{"tenantId":"t"}""")]
    [InlineData("image-a", "asd", "rule-a", """{"tenantId":"u"}""")]
    [InlineData("image-a", "asd", "rule-a", """{"tenantId":"t","extra":null}""")]
    public void AnyDifferentInputChangesTheId(string imageId, string pipelineId, string ruleId, string runParams)
    {
        var baseline = DispatchIdentity.Create("image-a", "asd", "rule-a", Parse("""{"tenantId":"t"}"""));

        Assert.NotEqual(baseline, DispatchIdentity.Create(imageId, pipelineId, ruleId, Parse(runParams)));
    }

    [Fact]
    public void ArrayOrderIsSignificant()
    {
        Assert.NotEqual(
            DispatchIdentity.Create("image-a", "asd", "rule-a", Parse("""{"algorithmNames":["FindAir","Rpn"]}""")),
            DispatchIdentity.Create("image-a", "asd", "rule-a", Parse("""{"algorithmNames":["Rpn","FindAir"]}""")));
    }

    [Fact]
    public void MissingIdentifiersAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => DispatchIdentity.Create("", "asd", "rule-a", Parse("{}")));
        Assert.ThrowsAny<ArgumentException>(() => DispatchIdentity.Create("image-a", " ", "rule-a", Parse("{}")));
        Assert.ThrowsAny<ArgumentException>(() => DispatchIdentity.Create("image-a", "asd", "", Parse("{}")));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
