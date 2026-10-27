using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunZeroResolutionTests
{
    [Theory]
    [InlineData(AutoRunSourceMode.Filters)]
    [InlineData(AutoRunSourceMode.Rules)]
    public void Zero_minimum_is_kept_as_an_inclusive_provider_bound(AutoRunSourceMode mode)
    {
        var filters = new AutoRunFilters { Resolution = new() { Min = 0, Max = 20 } };
        Assert.Empty(ProviderQueryBuilder.Validate(filters));
        var builder = new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions()));
        var query = Assert.Single(builder.Build(new() { Mode = mode, Filters = filters },
            mode == AutoRunSourceMode.Rules ? [new PipelineRuleDocument { MatchAll = true }] : []));
        var range = Assert.Single(query.GetProperty("filter").GetProperty("logical_operators")
            .GetProperty("AND").EnumerateArray()).GetProperty("range").GetProperty("RESOLUTION");
        var values = Assert.Single(range.GetProperty("values").EnumerateArray());
        Assert.Equal(0, values.GetProperty("gte").GetDouble());
        Assert.Equal(20, values.GetProperty("lte").GetDouble());
    }

    [Theory]
    [InlineData(0d, null, true)]
    [InlineData(null, 1d, true)]
    [InlineData(0.5, 0.5, true)]
    [InlineData(-0.1, 1d, false)]
    [InlineData(0d, 0d, false)]
    [InlineData(2d, 1d, false)]
    [InlineData(null, null, false)]
    [InlineData(double.NaN, 1d, false)]
    [InlineData(0d, double.PositiveInfinity, false)]
    public void Minimum_can_be_zero_but_maximum_remains_positive_and_ranges_ordered(double? min, double? max, bool valid)
    {
        var errors = ProviderQueryBuilder.Validate(new() { Resolution = new() { Min = min, Max = max } });
        Assert.Equal(valid, !errors.Any());
    }
}
