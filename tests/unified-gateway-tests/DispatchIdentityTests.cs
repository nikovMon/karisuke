using System.Text.Json;
using ImagingPipeline.UnifiedGateway.Dispatch;

namespace ImagingPipeline.UnifiedGateway.Tests;

public sealed class DispatchIdentityTests
{
    [Fact]
    public void IdIsAFixedLengthTransportSafeHash()
    {
        var id = DispatchIdentity.Create("image-a", "asd", "rule-a", Parse("""{"tenantId":"t"}"""));

        Assert.Matches("^[0-9a-f]{64}$", id);
    }

    [Theory]
    [InlineData('a')]
    [InlineData('\u05d0')]
    public void LongIdentifiersCannotExceedTheAmqpMessageIdLimit(char character)
    {
        var id = DispatchIdentity.Create(new string(character, 1000), "asd", new string(character, 1000), Parse("{}"));

        Assert.Equal(64, System.Text.Encoding.UTF8.GetByteCount(id));
    }

    [Fact]
    public void IdentifierSeparatorsCannotCollideAcrossTupleBoundaries()
    {
        Assert.NotEqual(
            DispatchIdentity.Create("image:asd", "algo", "rule", Parse("{}")),
            DispatchIdentity.Create("image", "asd", "algo:rule", Parse("{}")));
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

    [Fact]
    public void HistoricalIdentityIsCanonicalStableWithinRunAndSeparateFromLive()
    {
        var parameters = Parse("""{"tenantId":"t","config":{"a":1,"b":2}}""");
        var reordered = Parse("""{"config":{"b":2,"a":1},"tenantId":"t"}""");
        var identity = DispatchIdentity.CreateHistorical("run-a", "image-a", "asd", "rule-a", parameters);

        Assert.Matches("^[0-9a-f]{64}$", identity);
        Assert.Equal(identity, DispatchIdentity.CreateHistorical("run-a", "image-a", "asd", "rule-a", reordered));
        Assert.NotEqual(identity, DispatchIdentity.CreateHistorical("run-b", "image-a", "asd", "rule-a", parameters));
        Assert.NotEqual(identity, DispatchIdentity.Create("image-a", "asd", "rule-a", parameters));
        Assert.NotEqual(identity, DispatchIdentity.CreateHistorical("run-a", "image-b", "asd", "rule-a", parameters));
        Assert.NotEqual(identity, DispatchIdentity.CreateHistorical("run-a", "image-a", "other", "rule-a", parameters));
        Assert.NotEqual(identity, DispatchIdentity.CreateHistorical("run-a", "image-a", "asd", "rule-b", parameters));
        Assert.NotEqual(identity, DispatchIdentity.CreateHistorical("run-a", "image-a", "asd", "rule-a", Parse("{}")));
        Assert.ThrowsAny<ArgumentException>(() => DispatchIdentity.CreateHistorical("", "image-a", "asd", "rule-a", parameters));
    }

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement.Clone();
}
