using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunSensorNameQueryTests
{
    [Fact]
    public async Task Rules_and_filters_use_one_canonical_snapshot_without_changing_saved_values()
    {
        var catalog = new Catalog { Snapshot = Names("aRe", "Brt") };
        var builder = Builder(catalog);
        var source = new AutoRunSource
        {
            Mode = AutoRunSourceMode.Rules,
            Filters = new() { SensorNames = [" ARE ", "brt", "arE"] }
        };
        var rules = new[]
        {
            Rule("arE", RegistrationQuality.Accurate, "grid-A"),
            Rule("Are", RegistrationQuality.Sensor, "grid-B")
        };
        var original = JsonSerializer.Serialize(new { source, rules });

        var queries = await builder.BuildAsync(source, rules, CancellationToken.None);

        Assert.Equal(1, catalog.Calls);
        Assert.Equal(2, queries.Count);
        foreach (var query in queries)
        {
            var sensors = Clauses(query, "SENSOR");
            Assert.Equal(new[] { "aRe", "Brt" }, sensors[0].EnumerateArray().Select(value => value.GetString()));
            Assert.Equal("aRe", Assert.Single(sensors[1].EnumerateArray()).GetString());
        }
        Assert.True(Assert.Single(Clauses(queries[0], "hasalgorithmicregistration"))[0].GetBoolean());
        Assert.False(Assert.Single(Clauses(queries[1], "hasalgorithmicregistration"))[0].GetBoolean());
        Assert.Equal("grid-A", Assert.Single(Clauses(queries[0], "gridType.keyword"))[0].GetString());
        Assert.Equal("grid-B", Assert.Single(Clauses(queries[1], "gridType.keyword"))[0].GetString());
        Assert.Equal(original, JsonSerializer.Serialize(new { source, rules }));
    }

    [Fact]
    public async Task Equivalent_rule_branches_are_deduplicated_after_name_resolution()
    {
        var builder = Builder(new Catalog { Snapshot = Names("aRe") });
        var query = Assert.Single(await builder.BuildAsync(new() { Mode = AutoRunSourceMode.Rules },
            [Rule("arE"), Rule("Are")], CancellationToken.None));

        Assert.Equal("aRe", Assert.Single(Clauses(query, "SENSOR"))[0].GetString());
    }

    [Fact]
    public async Task Later_preparations_use_refreshed_spelling_and_frozen_queries_keep_their_values()
    {
        var catalog = new Catalog { Snapshot = Names("Ary") };
        var builder = Builder(catalog);
        var source = new AutoRunSource { Mode = AutoRunSourceMode.Filters, Filters = new() { SensorNames = ["aRY"] } };

        var first = Assert.Single(await builder.BuildAsync(source, [], CancellationToken.None));
        catalog.Snapshot = Names("aRy");
        var second = Assert.Single(await builder.BuildAsync(source, [], CancellationToken.None));

        Assert.Equal("Ary", Assert.Single(Clauses(first, "SENSOR"))[0].GetString());
        Assert.Equal("aRy", Assert.Single(Clauses(second, "SENSOR"))[0].GetString());
        Assert.Equal(2, catalog.Calls);
    }

    [Theory]
    [InlineData(AutoRunSourceMode.Filters)]
    [InlineData(AutoRunSourceMode.Rules)]
    public async Task Unknown_names_fail_query_preparation_when_the_catalog_is_enabled(AutoRunSourceMode mode)
    {
        var builder = Builder(new Catalog { Snapshot = Names("Ary") });
        var source = new AutoRunSource
        {
            Mode = mode,
            Filters = mode == AutoRunSourceMode.Filters ? new() { SensorNames = ["missing"] } : null
        };

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(source,
            mode == AutoRunSourceMode.Rules ? [Rule("missing")] : [], CancellationToken.None));

        Assert.Contains("Sensor name 'missing' was not found in the sensor catalog", error.Message);
    }

    [Fact]
    public async Task Unconfigured_catalog_preserves_query_names()
    {
        var query = Assert.Single(await Builder(new Catalog()).BuildAsync(new() { Mode = AutoRunSourceMode.Rules },
            [Rule("arE")], CancellationToken.None));

        Assert.Equal("arE", Assert.Single(Clauses(query, "SENSOR"))[0].GetString());
    }

    [Fact]
    public async Task Id_lookups_and_searches_without_sensor_names_do_not_require_the_catalog()
    {
        var catalog = new Catalog { Failure = new ElasticProviderException("Catalog unavailable.") };
        var builder = Builder(catalog);

        Assert.Empty(await builder.BuildAsync(new() { Mode = AutoRunSourceMode.OverlayIds }, [Rule("Ary")], CancellationToken.None));
        Assert.Single(await builder.BuildAsync(new() { Mode = AutoRunSourceMode.Filters }, [], CancellationToken.None));
        Assert.Single(await builder.BuildAsync(new() { Mode = AutoRunSourceMode.Rules },
            [new() { MatchAll = true }], CancellationToken.None));
        Assert.Equal(0, catalog.Calls);
    }

    [Fact]
    public async Task Cancellation_is_forwarded_to_initial_catalog_loading()
    {
        using var cancellation = new CancellationTokenSource();
        var catalog = new Catalog { Snapshot = Names("Ary") };
        await Builder(catalog).BuildAsync(new() { Mode = AutoRunSourceMode.Rules }, [Rule("ary")], cancellation.Token);
        Assert.Equal(cancellation.Token, catalog.Token);

        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Builder(catalog).BuildAsync(
            new() { Mode = AutoRunSourceMode.Rules }, [Rule("ary")], cancellation.Token));
        Assert.Equal(1, catalog.Calls);
    }

    private static ProviderQueryBuilder Builder(ISensorNameCatalog catalog) =>
        new(Options.Create(new ElasticProviderOptions()), sensorCatalog: catalog);

    private static Dictionary<string, string> Names(params string[] names) =>
        names.ToDictionary(name => name, name => name, StringComparer.OrdinalIgnoreCase);

    private static PipelineRuleDocument Rule(string name,
        RegistrationQuality quality = RegistrationQuality.Accurate, string grid = "grid") =>
        new() { Match = new() { Sensors = [new() { Name = name, RegistrationQualities = [quality], GridTypes = [grid] }] } };

    private static JsonElement[] Clauses(JsonElement query, string field) =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray()
            .Where(clause => clause.TryGetProperty("match", out var match) && match.TryGetProperty(field, out _))
            .Select(clause => clause.GetProperty("match").GetProperty(field).GetProperty("values")).ToArray();

    private sealed class Catalog : ISensorNameCatalog
    {
        public IReadOnlyDictionary<string, string>? Snapshot { get; set; }
        public Exception? Failure { get; init; }
        public int Calls { get; private set; }
        public CancellationToken Token { get; private set; }

        public Task<IReadOnlyDictionary<string, string>?> GetSnapshotAsync(CancellationToken cancellationToken)
        {
            Calls++;
            Token = cancellationToken;
            return Failure is { } error ? Task.FromException<IReadOnlyDictionary<string, string>?>(error) : Task.FromResult(Snapshot);
        }
    }
}
