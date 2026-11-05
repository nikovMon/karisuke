using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunResolutionQueryTests
{
    [Theory]
    [InlineData(AutoRunSourceMode.Filters, 80.0, 80.0, 0.8, 0.8)]
    [InlineData(AutoRunSourceMode.Rules, 80.0, 80.0, 0.8, 0.8)]
    [InlineData(AutoRunSourceMode.Filters, 80.0, null, 0.8, null)]
    [InlineData(AutoRunSourceMode.Rules, 80.0, null, 0.8, null)]
    [InlineData(AutoRunSourceMode.Filters, null, 80.0, null, 0.8)]
    [InlineData(AutoRunSourceMode.Rules, null, 80.0, null, 0.8)]
    [InlineData(AutoRunSourceMode.Filters, 0.0, 80.0, 0.0, 0.8)]
    [InlineData(AutoRunSourceMode.Filters, 0.0, null, 0.0, null)]
    public void Resolution_bounds_use_provider_units_without_changing_saved_values(AutoRunSourceMode mode,
        double? minimum, double? maximum, double? expectedMinimum, double? expectedMaximum)
    {
        var source = new AutoRunSource
        {
            Mode = mode,
            Filters = mode == AutoRunSourceMode.Filters ? new() { Resolution = new() { Min = minimum, Max = maximum } } : null
        };
        PipelineRuleDocument[] rules = mode == AutoRunSourceMode.Rules
            ? [new() { Match = new() { Resolution = new() { Min = minimum, Max = maximum } } }] : [];
        var original = JsonSerializer.Serialize(new { source, rules });
        var query = Assert.Single(Builder().Build(source, rules));
        var range = Assert.Single(Ranges(query, "RESOLUTION"));

        AssertBound(range, "gte", expectedMinimum);
        AssertBound(range, "lte", expectedMaximum);
        Assert.Equal(original, JsonSerializer.Serialize(new { source, rules }));
    }

    [Fact]
    public void Rule_and_filter_ranges_scale_independently_with_custom_alias_and_leave_time_ranges_unchanged()
    {
        var options = new ElasticProviderOptions();
        options.RequestFields.Resolution = "custom_resolution";
        var source = new AutoRunSource
        {
            Mode = AutoRunSourceMode.Rules,
            Filters = new()
            {
                Resolution = new() { Min = 60, Max = 80 },
                PhotoTime = new() { From = "2026-10-04T19:40:00+03:00", To = "2026-10-05T16:43:00Z" },
                CreatedTime = new() { From = "2026-10-01T00:00:00Z" }
            }
        };
        PipelineRuleDocument[] rules = [new() { Match = new() { Resolution = new() { Min = 40, Max = 80 } } }];
        var original = JsonSerializer.Serialize(new { source, rules });
        var query = Assert.Single(Builder(options).Build(source, rules));
        var ranges = Ranges(query, "custom_resolution");

        Assert.Equal(2, ranges.Length);
        AssertBound(ranges[0], "gte", 0.6);
        AssertBound(ranges[0], "lte", 0.8);
        AssertBound(ranges[1], "gte", 0.4);
        AssertBound(ranges[1], "lte", 0.8);
        var photo = Assert.Single(Ranges(query, "date"));
        Assert.Equal("2026-10-04T16:40:00.000Z", photo.GetProperty("gte").GetString());
        Assert.Equal("2026-10-05T16:43:00.000Z", photo.GetProperty("lte").GetString());
        var created = Assert.Single(Ranges(query, "CREATED"));
        Assert.Equal("2026-10-01T00:00:00.000Z", created.GetProperty("gte").GetString());
        Assert.False(created.TryGetProperty("lte", out _));
        Assert.Equal(original, JsonSerializer.Serialize(new { source, rules }));
    }

    private static ProviderQueryBuilder Builder(ElasticProviderOptions? options = null) => new(Options.Create(options ?? new()));

    private static JsonElement[] Ranges(JsonElement query, string field) =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray()
            .Where(clause => clause.TryGetProperty("range", out var range) && range.TryGetProperty(field, out _))
            .Select(clause => Assert.Single(clause.GetProperty("range").GetProperty(field).GetProperty("values").EnumerateArray())).ToArray();

    private static void AssertBound(JsonElement range, string name, double? expected)
    {
        Assert.Equal(expected.HasValue, range.TryGetProperty(name, out var bound));
        if (expected is { } value)
        {
            Assert.Equal(JsonValueKind.Number, bound.ValueKind);
            Assert.Equal(value, bound.GetDouble());
        }
    }
}
