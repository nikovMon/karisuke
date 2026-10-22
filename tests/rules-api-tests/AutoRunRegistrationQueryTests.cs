using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunRegistrationQueryTests
{
    [Theory]
    [InlineData("Accurate", true)]
    [InlineData("Sensor", false)]
    public void Explicit_quality_filter_uses_confirmed_boolean_flag(string quality, bool expected)
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new() { RegistrationQualities = [quality] }
        }, []));

        var value = Assert.Single(Values(query));
        Assert.Equal(expected ? JsonValueKind.True : JsonValueKind.False, value.ValueKind);
        Assert.Equal(expected, value.GetBoolean());
    }

    [Fact]
    public void Both_qualities_become_two_native_booleans_without_duplicates()
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new() { RegistrationQualities = [" Accurate ", "Sensor", "Accurate"] }
        }, []));

        Assert.Equal(new[] { true, false }, Values(query).Select(value => value.GetBoolean()));
    }

    [Fact]
    public void Sensor_specific_qualities_stay_in_their_own_query_branch()
    {
        var queries = Builder().Build(new() { Mode = AutoRunSourceMode.Rules },
        [new()
        {
            Match = new()
            {
                Sensors =
                [
                    new() { Name = "accurate-camera", RegistrationQualities = [RegistrationQuality.Accurate] },
                    new() { Name = "sensor-camera", RegistrationQualities = [RegistrationQuality.Sensor] },
                    new() { Name = "either-camera", RegistrationQualities = [RegistrationQuality.Accurate, RegistrationQuality.Sensor] }
                ]
            }
        }]);

        Assert.Equal(3, queries.Count);
        Assert.Equal(new[] { true }, Values(queries[0]).Select(value => value.GetBoolean()));
        Assert.Equal(new[] { false }, Values(queries[1]).Select(value => value.GetBoolean()));
        Assert.Equal(new[] { true, false }, Values(queries[2]).Select(value => value.GetBoolean()));
        for (var i = 0; i < queries.Count; i++)
        {
            var expectedSensor = new[] { "accurate-camera", "sensor-camera", "either-camera" }[i];
            Assert.Equal(expectedSensor, Assert.Single(Values(queries[i], "SENSOR")).GetString());
        }
    }

    [Fact]
    public void Optional_quality_filter_remains_ANDed_with_each_rules_sensor_quality()
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Rules,
            Filters = new() { RegistrationQualities = ["Sensor"] }
        }, [new()
        {
            Match = new() { Sensors = [new() { Name = "camera", RegistrationQualities = [RegistrationQuality.Accurate] }] }
        }]));

        // Contradictory conditions must stay contradictory; merging them into [true, false] would broaden the search.
        var qualities = Clauses(query).Where(clause => clause.TryGetProperty("match", out var match) &&
            match.TryGetProperty("hasalgorithmicregistration", out _)).ToArray();
        Assert.Equal(2, qualities.Length);
        Assert.False(qualities[0].GetProperty("match").GetProperty("hasalgorithmicregistration").GetProperty("values")[0].GetBoolean());
        Assert.True(qualities[1].GetProperty("match").GetProperty("hasalgorithmicregistration").GetProperty("values")[0].GetBoolean());
    }

    [Fact]
    public void Request_alias_changes_only_the_field_name_and_response_mapping_is_unchanged()
    {
        var options = new ElasticProviderOptions();
        options.RequestFields.RegistrationQuality = "HASALGORITHMICREGISTRATION";
        var query = Assert.Single(Builder(options).Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new() { RegistrationQualities = ["Accurate", "Sensor"] }
        }, []));

        Assert.Equal(new[] { true, false }, Values(query, "HASALGORITHMICREGISTRATION").Select(value => value.GetBoolean()));
        Assert.Equal("RegistrationQuality", options.ResponseFields.RegistrationQuality);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    [InlineData("accurate")]
    public void Public_quality_filter_still_rejects_invalid_enum_names(string quality)
    {
        Assert.Throws<ArgumentException>(() => Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new() { RegistrationQualities = [quality] }
        }, []));
    }

    private static ProviderQueryBuilder Builder(ElasticProviderOptions? options = null) =>
        new(Options.Create(options ?? new()));

    private static JsonElement[] Clauses(JsonElement query) =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray().ToArray();

    private static JsonElement[] Values(JsonElement query, string field = "hasalgorithmicregistration") =>
        Clauses(query).Single(clause => clause.TryGetProperty("match", out var match) && match.TryGetProperty(field, out _))
            .GetProperty("match").GetProperty(field).GetProperty("values").EnumerateArray().ToArray();
}
