using System.Globalization;
using ImagingPipeline.RabbitMqConfiguration;

namespace ImagingPipeline.RabbitMqClient.Tests;

public sealed class RabbitMqArgumentNormalizerTests
{
    [Theory]
    [InlineData("x-dead-letter-exchange")]
    [InlineData("x-dead-letter-routing-key")]
    [InlineData("x-queue-type")]
    [InlineData("x-overflow")]
    [InlineData("x-match")]
    [InlineData("alternate-exchange")]
    [InlineData("x-delayed-type")]
    public void KnownStringArgumentsPreserveNumericAndBooleanLookingValues(string key)
    {
        foreach (var policy in Enum.GetValues<RabbitMqArgumentConversionPolicy>())
        foreach (var value in new[] { "123", "true", "", "2147483648", "1.5" })
        {
            var normalized = RabbitMqArgumentNormalizer.Normalize(new Dictionary<string, object?> { [key] = value }, policy);

            Assert.Equal(value, Assert.IsType<string>(normalized[key]));
        }
    }

    [Theory]
    [InlineData("2147483648")]
    [InlineData("1.25")]
    [InlineData("1e3")]
    public void LegacyPolicyDoesNotChangeArbitraryLargeOrFloatingPointBindingStrings(string value)
    {
        var normalized = RabbitMqArgumentNormalizer.Normalize(
            new Dictionary<string, object?> { ["custom-header"] = value }, RabbitMqArgumentConversionPolicy.Legacy);

        Assert.Equal(value, Assert.IsType<string>(normalized["custom-header"]));
    }

    [Theory]
    [InlineData(RabbitMqArgumentConversionPolicy.Legacy)]
    [InlineData(RabbitMqArgumentConversionPolicy.ExtendedNumeric)]
    public void ConversionIsInvariantAndRepeatedNormalizationIsStable(RabbitMqArgumentConversionPolicy policy)
    {
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            var culture = (CultureInfo)CultureInfo.GetCultureInfo("fr-FR").Clone();
            culture.NumberFormat.NegativeSign = "~";
            CultureInfo.CurrentCulture = culture;
            var source = new Dictionary<string, object?>
            {
                ["integer"] = "-42", ["boolean"] = "True", ["large"] = "2147483648",
                ["fraction"] = "1.25", ["exponent"] = "1e3", ["localized"] = "1,25",
                ["localized-sign"] = "~42", ["nonfinite"] = "NaN", ["overflow"] = "1e400",
                ["x-dead-letter-routing-key"] = "123"
            };

            var normalized = RabbitMqArgumentNormalizer.Normalize(source, policy);
            var repeated = RabbitMqArgumentNormalizer.Normalize(normalized, policy);

            Assert.Equal(-42, Assert.IsType<int>(normalized["integer"]));
            Assert.True(Assert.IsType<bool>(normalized["boolean"]));
            if (policy == RabbitMqArgumentConversionPolicy.ExtendedNumeric)
            {
                Assert.Equal(2147483648L, Assert.IsType<long>(normalized["large"]));
                Assert.Equal(1.25, Assert.IsType<double>(normalized["fraction"]));
                Assert.Equal(1000d, Assert.IsType<double>(normalized["exponent"]));
            }
            else
            {
                Assert.Equal("2147483648", normalized["large"]);
                Assert.Equal("1.25", normalized["fraction"]);
                Assert.Equal("1e3", normalized["exponent"]);
            }
            foreach (var key in new[] { "localized", "localized-sign", "nonfinite", "overflow", "x-dead-letter-routing-key" })
                Assert.Equal(source[key], Assert.IsType<string>(normalized[key]));
            Assert.Equal(normalized, repeated);
            Assert.Equal("-42", source["integer"]);
            normalized.Clear();
            Assert.Equal(10, source.Count);
            Assert.Equal(10, repeated.Count);
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
        }
    }

    [Theory]
    [InlineData(RabbitMqArgumentConversionPolicy.Legacy)]
    [InlineData(RabbitMqArgumentConversionPolicy.ExtendedNumeric)]
    public void TypedValuesAndNestedRuntimeTablesPassThroughWithoutMutation(RabbitMqArgumentConversionPolicy policy)
    {
        var nested = new Dictionary<string, object?> { ["custom"] = "123" };
        var source = new Dictionary<string, object?>
        {
            ["int"] = 3, ["long"] = 4L, ["double"] = 1.25, ["boolean"] = true,
            ["decimal"] = 1.25m, ["bytes"] = new byte[] { 1, 2 }, ["nested"] = nested,
            ["array"] = new object[] { "3", 4 }, ["null"] = null,
            ["x-dead-letter-exchange"] = new byte[] { 49, 50, 51 }
        };

        var normalized = RabbitMqArgumentNormalizer.Normalize(source, policy);

        Assert.NotSame(source, normalized);
        foreach (var entry in source) Assert.Same(entry.Value, normalized[entry.Key]);
        Assert.Equal("123", nested["custom"]);
        normalized.Remove("int");
        Assert.True(source.ContainsKey("int"));
    }
}
