using ImagingPipeline.Observability;

namespace ImagingPipeline.Observability.Tests;

public sealed class FindAirMessageHeadersTests
{
    [Theory]
    [InlineData("FindAir", "FindAir")]
    [InlineData("Rpn", "Rpn")]
    [InlineData("Rpn,FindAir", "FindAir,Rpn")]
    [InlineData("FindAir,Rpn", "FindAir,Rpn")]
    public void ForwardCreatesCanonicalRoutingHeader(string sourceValue, string expected)
    {
        var source = new Dictionary<string, object?>
        {
            ["traceparent"] = "trace-context",
            ["business-header"] = "must-not-propagate",
            ["algorithm_name"] = "legacy"
        };

        var result = FindAirMessageHeaders.Forward(source, sourceValue);

        Assert.Equal(expected, result[FindAirMessageHeaders.AlgorithmName]);
        Assert.Equal("trace-context", result[FindAirMessageHeaders.TraceParent]);
        Assert.DoesNotContain("business-header", result.Keys);
        Assert.DoesNotContain("algorithm_name", result.Keys);
    }
}
