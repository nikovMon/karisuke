using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunPhotoAgeQueryTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T10:20:30.456Z");

    [Fact]
    public void Different_rule_limits_create_different_queries_and_no_limit_adds_no_date_constraint()
    {
        var queries = Builder().Build(new() { Mode = AutoRunSourceMode.Rules },
            [Rule(1), Rule(7), Rule(null)]);

        Assert.Equal(3, queries.Count);
        Assert.Equal("2026-10-26T10:20:30.456Z", Range(queries[0]).GetProperty("gte").GetString());
        Assert.Equal("2026-10-20T10:20:30.456Z", Range(queries[1]).GetProperty("gte").GetString());
        Assert.All(queries.Take(2), query => Assert.False(Range(query).TryGetProperty("lte", out _)));
        Assert.Empty(Clauses(queries[2]));
        Assert.All(queries, query => Assert.Equal("desc", query.GetProperty("sort").GetProperty("date").GetString()));
    }

    [Fact]
    public void All_sensor_branches_capture_one_clock_value_and_identical_queries_are_deduplicated()
    {
        var clock = new AdvancingTimeProvider();
        var rule = Rule(3);
        rule.Match!.Sensors = [new() { Name = "A" }, new() { Name = "B" }];
        var queries = Builder(clock: clock).Build(new() { Mode = AutoRunSourceMode.Rules }, [rule, rule, Rule(7)]);

        Assert.Equal(1, clock.ReadCount);
        Assert.Equal(3, queries.Count);
        Assert.Equal("2026-10-24T10:20:30.456Z", Range(queries[0]).GetProperty("gte").GetString());
        Assert.Equal("2026-10-24T10:20:30.456Z", Range(queries[1]).GetProperty("gte").GetString());
        Assert.Equal("2026-10-20T10:20:30.456Z", Range(queries[2]).GetProperty("gte").GetString());
        Assert.Equal("A", Sensor(queries[0]));
        Assert.Equal("B", Sensor(queries[1]));
        Assert.Equal(3, rule.Match.MaxPhotoAgeDays);
    }

    [Theory]
    [InlineData("2026-10-01T12:20:30.456+02:00", "2026-10-20T10:20:30.456Z")]
    [InlineData("2026-10-25T12:20:30.456+02:00", "2026-10-25T10:20:30.456Z")]
    public void Optional_photo_range_intersects_rule_age_and_preserves_other_filters(string from, string expectedFrom)
    {
        var filters = new AutoRunFilters
        {
            PhotoTime = new() { From = from, To = "2026-10-28T13:40:00.123+03:00" },
            CreatedTime = new() { From = "2026-10-01T00:00:00Z" },
            SensorNames = ["ar"], Resolution = new() { Min = 1, Max = 20 }
        };
        var rule = Rule(7);
        var originalFilters = JsonSerializer.Serialize(filters);
        var originalRule = JsonSerializer.Serialize(rule);

        var query = Assert.Single(Builder().Build(new() { Mode = AutoRunSourceMode.Rules, Filters = filters }, [rule]));

        var range = Range(query);
        Assert.Equal(expectedFrom, range.GetProperty("gte").GetString());
        Assert.Equal("2026-10-28T10:40:00.123Z", range.GetProperty("lte").GetString());
        Assert.Equal("2026-10-01T00:00:00.000Z", Range(query, "CREATED").GetProperty("gte").GetString());
        Assert.Equal("ar", Sensor(query));
        Assert.Equal(1, Range(query, "RESOLUTION").GetProperty("gte").GetInt32());
        Assert.Equal(20, Range(query, "RESOLUTION").GetProperty("lte").GetInt32());
        Assert.Equal(originalFilters, JsonSerializer.Serialize(filters));
        Assert.Equal(originalRule, JsonSerializer.Serialize(rule));
    }

    [Fact]
    public void Disjoint_user_range_omits_only_the_rule_branch_that_cannot_match()
    {
        var filters = new AutoRunFilters
        {
            PhotoTime = new() { From = "2026-10-20T00:00:00Z", To = "2026-10-23T00:00:00Z" }
        };
        var queries = Builder().Build(new() { Mode = AutoRunSourceMode.Rules, Filters = filters },
            [Rule(1), Rule(7), Rule(null)]);

        Assert.Equal(2, queries.Count);
        Assert.Equal("2026-10-20T10:20:30.456Z", Range(queries[0]).GetProperty("gte").GetString());
        Assert.Equal("2026-10-20T00:00:00.000Z", Range(queries[1]).GetProperty("gte").GetString());
        Assert.All(queries, query => Assert.Equal("2026-10-23T00:00:00.000Z", Range(query).GetProperty("lte").GetString()));
        Assert.Empty(Builder().Build(new() { Mode = AutoRunSourceMode.Rules, Filters = filters }, [Rule(1)]));
    }

    [Fact]
    public void Equal_lower_and_upper_bounds_remain_an_inclusive_query()
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Rules,
            Filters = new() { PhotoTime = new() { To = "2026-10-26T10:20:30.456Z" } }
        }, [Rule(1)]));

        Assert.Equal("2026-10-26T10:20:30.456Z", Range(query).GetProperty("gte").GetString());
        Assert.Equal("2026-10-26T10:20:30.456Z", Range(query).GetProperty("lte").GetString());
    }

    [Theory]
    [InlineData("date")]
    [InlineData("DATE")]
    [InlineData("custom_photo_time")]
    public void Rule_age_uses_configured_photo_time_alias_and_UTC_milliseconds(string field)
    {
        var options = new ElasticProviderOptions();
        options.RequestFields.PhotoTime = field;
        var clock = new FixedTimeProvider(DateTimeOffset.Parse("2026-10-27T13:20:30.456+03:00"));

        var query = Assert.Single(Builder(options, clock).Build(new() { Mode = AutoRunSourceMode.Rules }, [Rule(1)]));

        Assert.Equal("2026-10-26T10:20:30.456Z", Range(query, field).GetProperty("gte").GetString());
        Assert.DoesNotContain("PHOTOTIME", query.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Filters_mode_does_not_apply_selected_rule_age_globally()
    {
        var query = Assert.Single(Builder().Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new() { PhotoTime = new() { From = "2020-01-01T00:00:00Z" } }
        }, [Rule(1)]));

        Assert.Equal("2020-01-01T00:00:00.000Z", Range(query).GetProperty("gte").GetString());
        Assert.False(Range(query).TryGetProperty("lte", out _));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(int.MaxValue)]
    public void Invalid_age_is_rejected_before_building_queries(int days) =>
        Assert.Throws<ArgumentException>(() => Builder().Build(new() { Mode = AutoRunSourceMode.Rules }, [Rule(days)]));

    [Fact]
    public void Very_large_valid_age_uses_earliest_timestamp_without_overflow()
    {
        var query = Assert.Single(Builder().Build(new() { Mode = AutoRunSourceMode.Rules }, [Rule(3_000_000)]));

        Assert.Equal("0001-01-01T00:00:00.000Z", Range(query).GetProperty("gte").GetString());
    }

    private static ProviderQueryBuilder Builder(ElasticProviderOptions? options = null, TimeProvider? clock = null) =>
        new(Options.Create(options ?? new()), timeProvider: clock ?? new FixedTimeProvider(Now));

    private static PipelineRuleDocument Rule(int? age) => new()
    {
        Id = $"age-{age}", MatchAll = age is null,
        Match = age is null ? null : new() { MaxPhotoAgeDays = age }
    };

    private static JsonElement[] Clauses(JsonElement query) =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray().ToArray();

    private static JsonElement Range(JsonElement query, string field = "date")
    {
        var clause = Assert.Single(Clauses(query), clause =>
            clause.TryGetProperty("range", out var range) && range.TryGetProperty(field, out _));
        var condition = clause.GetProperty("range").GetProperty(field);
        Assert.Equal("IN", condition.GetProperty("type").GetString());
        return Assert.Single(condition.GetProperty("values").EnumerateArray());
    }

    private static string? Sensor(JsonElement query) =>
        Assert.Single(Clauses(query), clause => clause.TryGetProperty("match", out var match) && match.TryGetProperty("SENSOR", out _))
            .GetProperty("match").GetProperty("SENSOR").GetProperty("values")[0].GetString();

    private sealed class FixedTimeProvider(DateTimeOffset time) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => time;
    }

    private sealed class AdvancingTimeProvider : TimeProvider
    {
        public int ReadCount { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now.AddSeconds(ReadCount++);
    }
}
