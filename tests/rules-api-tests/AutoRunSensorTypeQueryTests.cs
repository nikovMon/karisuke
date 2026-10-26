using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunSensorTypeQueryTests
{
    [Theory]
    [InlineData("VIS")]
    [InlineData("PAN")]
    [InlineData("PAN_Sharpen_RGB")]
    [InlineData("PAN_MSI")]
    [InlineData("EO")]
    [InlineData("IR")]
    [InlineData("CIR")]
    [InlineData("NIR")]
    [InlineData("MWIR")]
    [InlineData("SAR")]
    [InlineData("SWIR")]
    public void Default_catalog_queries_original_types_on_keyword_field(string type)
    {
        var builder = Builder();
        var filterQuery = Assert.Single(builder.Build(new()
        {
            Mode = AutoRunSourceMode.Filters, Filters = new() { SensorTypes = [type] }
        }, []));
        var ruleQuery = Assert.Single(builder.Build(new() { Mode = AutoRunSourceMode.Rules }, [Rule(type)]));

        Assert.Equal(new[] { type }, Assert.Single(TypeClauses(filterQuery)));
        Assert.Equal(new[] { type }, Assert.Single(TypeClauses(ruleQuery)));
        Assert.DoesNotContain("IMAGINGTECHNIQUE", filterQuery.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Configured_query_values_are_deduplicated_without_changing_filters_or_saved_rules()
    {
        var options = new ElasticProviderOptions
        {
            SensorTypeMappings = new(StringComparer.Ordinal) { ["VIS"] = "visible", ["PAN"] = "visible", ["SAR"] = "radar" }
        };
        options.RequestFields.SensorType = "custom.sensorType";
        var builder = Builder(options);
        var filters = new AutoRunFilters { SensorTypes = [" VIS ", "PAN", "SAR"] };
        var rule = Rule("VIS", "PAN", "SAR");
        var saved = JsonSerializer.Serialize(rule);

        var explicitQuery = Assert.Single(builder.Build(new() { Mode = AutoRunSourceMode.Filters, Filters = filters }, []));
        var ruleQuery = Assert.Single(builder.Build(new() { Mode = AutoRunSourceMode.Rules }, [rule]));

        Assert.Equal(new[] { "visible", "radar" }, Assert.Single(TypeClauses(explicitQuery, "custom.sensorType")));
        Assert.Equal(new[] { "visible", "radar" }, Assert.Single(TypeClauses(ruleQuery, "custom.sensorType")));
        Assert.Equal(new[] { " VIS ", "PAN", "SAR" }, filters.SensorTypes);
        Assert.Equal(saved, JsonSerializer.Serialize(rule));
        Assert.True(ProviderQueryBuilder.Matches(Overlay("VIS"), filters));
        Assert.False(ProviderQueryBuilder.Matches(Overlay("visible"), filters));
    }

    [Fact]
    public void Rule_and_explicit_sensor_filters_remain_separate_AND_conditions()
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Rules, Filters = new() { SensorTypes = ["PAN"] }
        }, [Rule("VIS")]));

        var types = TypeClauses(query);
        Assert.Equal(2, types.Length);
        Assert.Equal(new[] { "PAN" }, types[0]);
        Assert.Equal(new[] { "VIS" }, types[1]);
    }

    [Theory]
    [InlineData("vis")]
    [InlineData("UNKNOWN")]
    [InlineData("PAN_sharpen_RGB")]
    public void Unknown_or_wrong_case_values_are_rejected_with_filter_or_rule_context(string type)
    {
        var builder = Builder();
        var filters = new AutoRunFilters { SensorTypes = [type] };
        var rule = Rule(type);
        var filterError = Assert.Single(builder.ValidateSensorTypes(filters));
        var ruleError = Assert.Single(builder.ValidateSensorTypes(null, [rule]));

        Assert.Contains("source.filters.sensorTypes", filterError);
        Assert.Contains($"'{type}'", filterError);
        Assert.Contains("case-sensitive", filterError);
        Assert.Contains("Rule 'rule-1' match.sensorTypes", ruleError);
        Assert.Contains($"'{type}'", ruleError);
        Assert.Throws<ArgumentException>(() => builder.Build(new() { Mode = AutoRunSourceMode.Filters, Filters = filters }, []));
        Assert.Throws<ArgumentException>(() => builder.Build(new() { Mode = AutoRunSourceMode.Rules }, [rule]));
        Assert.Throws<ArgumentException>(() => builder.Build(new() { Mode = AutoRunSourceMode.OverlayIds }, [rule]));
    }

    [Fact]
    public void Catalog_is_snapshotted_with_exact_case_even_if_configured_dictionary_ignores_case()
    {
        var options = new ElasticProviderOptions
        {
            SensorTypeMappings = new(StringComparer.OrdinalIgnoreCase) { ["VIS"] = "original-visible" }
        };
        var builder = Builder(options);
        options.SensorTypeMappings["VIS"] = "changed";
        options.SensorTypeMappings["PAN"] = "PAN";

        var query = Assert.Single(builder.Build(new()
        {
            Mode = AutoRunSourceMode.Filters, Filters = new() { SensorTypes = ["VIS"] }
        }, []));

        Assert.Equal(new[] { "original-visible" }, Assert.Single(TypeClauses(query)));
        Assert.NotEmpty(builder.ValidateSensorTypes(new() { SensorTypes = ["vis"] }));
        Assert.NotEmpty(builder.ValidateSensorTypes(new() { SensorTypes = ["PAN"] }));
    }

    [Fact]
    public void Prefilter_ignores_only_sensor_type_and_does_not_change_final_matching()
    {
        var overlay = Overlay("EO");
        var filters = new AutoRunFilters
        {
            SensorTypes = ["VIS"], SensorNames = ["camera"], IsRealtime = false,
            GridTypes = ["grid"], Resolution = new() { Min = 0.5, Max = 0.5 }
        };

        Assert.False(ProviderQueryBuilder.Matches(overlay, filters));
        Assert.True(ProviderQueryBuilder.MatchesWithoutSensorType(overlay, filters));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters));
        Assert.Equal(new[] { "VIS" }, filters.SensorTypes);
        Assert.False(ProviderQueryBuilder.MatchesWithoutSensorType(overlay, filters with { SensorNames = ["other"] }));
        Assert.False(ProviderQueryBuilder.MatchesWithoutSensorType(overlay, filters with { IsRealtime = true }));
        Assert.False(ProviderQueryBuilder.MatchesWithoutSensorType(overlay, filters with { Resolution = new() { Min = 0.6 } }));
    }

    private static ProviderQueryBuilder Builder(ElasticProviderOptions? options = null) => new(Options.Create(options ?? new()));

    private static PipelineRuleDocument Rule(params string[] types) =>
        new() { Id = "rule-1", Match = new() { SensorTypes = [.. types] } };

    private static string[][] TypeClauses(JsonElement query, string field = "sensorType.keyword") =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray()
            .Where(clause => clause.TryGetProperty("match", out var match) && match.TryGetProperty(field, out _))
            .Select(clause => clause.GetProperty("match").GetProperty(field).GetProperty("values")
                .EnumerateArray().Select(value => value.GetString()!).ToArray()).ToArray();

    private static ProviderOverlay Overlay(string type) => new([], new(
        "overlay-1", "camera", type, RegistrationQuality.Accurate, 0.5, null, "", 0, 0,
        DateTimeOffset.Parse("2026-10-26T00:00:00Z"),
        GeometryUtilities.ReadWkt("POLYGON ((34 31, 35 31, 35 32, 34 32, 34 31))"), "grid", ""), null, false, null);
}
