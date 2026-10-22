using System.Net;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderTests
{
    private const string Area = "POLYGON ((34 31, 35 31, 35 32, 34 32, 34 31))";

    [Fact]
    public void Explicit_query_uses_verified_grammar_and_native_false_and_inclusive_bounds()
    {
        var builder = new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions()));
        var query = Assert.Single(builder.Build(new AutoRunSource
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new AutoRunFilters
            {
                SensorNames = ["arc", "arc2"], IsRealtime = false,
                Resolution = new() { Min = 0.4, Max = 0.7 },
                CreatedTime = new() { From = "2026-10-19T10:00:00+03:00" },
                Wkt = Area
            }
        }, []));
        var clauses = Clauses(query);
        Assert.Equal("IN", clauses[0].GetProperty("match").GetProperty("SENSOR").GetProperty("type").GetString());
        Assert.Equal(new[] { "arc", "arc2" },
            clauses[0].GetProperty("match").GetProperty("SENSOR").GetProperty("values").EnumerateArray().Select(v => v.GetString()));
        Assert.Equal(JsonValueKind.False, Find(clauses, "match", "ISREALTIME")[0].ValueKind);
        var range = Find(clauses, "range", "RESOLUTION")[0];
        Assert.Equal(0.4, range.GetProperty("gte").GetDouble());
        Assert.Equal(0.7, range.GetProperty("lte").GetDouble());
        Assert.Equal("2026-10-19T07:00:00.000Z",
            Find(clauses, "range", "CREATED")[0].GetProperty("gte").GetString());
        Assert.Equal(Area, Find(clauses, "geo_intersect", "geo")[0].GetString());
        Assert.Equal("desc", query.GetProperty("sort").GetProperty("date").GetString());
        Assert.DoesNotContain("simpleOperators", query.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public void Rule_queries_keep_sensor_conditions_together_and_do_not_apply_photo_age()
    {
        var rule = new PipelineRuleDocument
        {
            Match = new()
            {
                Sensors =
                [
                    new() { Name = "A", GridTypes = ["grid-A"], RegistrationQualities = [RegistrationQuality.Accurate] },
                    new() { Name = "B", GridTypes = ["grid-B"], RegistrationQualities = [RegistrationQuality.Sensor] }
                ],
                Resolution = new() { Max = 1 }, LocationWkt = Area, MaxPhotoAgeDays = 1, ModeSar = ["strip"]
            }
        };
        var builder = new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions()));
        var queries = builder.Build(new() { Mode = AutoRunSourceMode.Rules }, [rule, rule]);
        Assert.Equal(2, queries.Count);
        var first = Clauses(queries[0]); var second = Clauses(queries[1]);
        Assert.Equal("A", Find(first, "match", "SENSOR")[0].GetString());
        Assert.Equal("grid-A", Find(first, "match", "SOLUTIONTYPE")[0].GetString());
        Assert.True(Find(first, "match", "hasalgorithmicregistration")[0].GetBoolean());
        Assert.Equal("B", Find(second, "match", "SENSOR")[0].GetString());
        Assert.Equal("grid-B", Find(second, "match", "SOLUTIONTYPE")[0].GetString());
        Assert.False(Find(second, "match", "hasalgorithmicregistration")[0].GetBoolean());
        Assert.All(queries, query =>
        {
            Assert.DoesNotContain("\"date\"", query.GetProperty("filter").GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("strip", query.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("\"OR\"", query.GetRawText(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("date", "CREATED", false)]
    [InlineData("DATE", "created", true)]
    [InlineData("date", "updated", false)]
    public void Request_aliases_preserve_native_numbers_booleans_and_UTC_date_ranges(
        string photoField, string createdField, bool realtime)
    {
        var options = new ElasticProviderOptions
        {
            RequestFields = new()
            {
                Resolution = "Resolution", IsRealtime = "IsRealTime", ModeSar = "ModeSAR",
                PhotoTime = photoField, CreatedTime = createdField
            }
        };
        var builder = new ProviderQueryBuilder(Options.Create(options));
        var query = Assert.Single(builder.Build(new()
        {
            Mode = AutoRunSourceMode.Rules,
            Filters = new()
            {
                IsRealtime = realtime, Resolution = new() { Min = 0.5 },
                PhotoTime = new() { From = "2026-10-04T19:40:00.123+03:00", To = "2026-10-04T16:43:00.999Z" },
                CreatedTime = new() { From = "2026-10-05T05:40:00Z", To = "2026-10-05T01:40:00.456-04:00" }
            }
        }, [new() { Match = new() { ModeSar = ["strip"] } }]));
        var clauses = Clauses(query);
        Assert.Equal(realtime ? JsonValueKind.True : JsonValueKind.False, Find(clauses, "match", "IsRealTime")[0].ValueKind);
        var range = Find(clauses, "range", "Resolution")[0];
        Assert.Equal(JsonValueKind.Object, range.ValueKind);
        Assert.Equal(JsonValueKind.Number, range.GetProperty("gte").ValueKind);
        Assert.Equal(0.5, range.GetProperty("gte").GetDouble());
        Assert.Equal("strip", Find(clauses, "match", "ModeSAR")[0].GetString());
        Assert.Equal("2026-10-04T16:40:00.123Z", Find(clauses, "range", photoField)[0].GetProperty("gte").GetString());
        Assert.Equal("2026-10-04T16:43:00.999Z", Find(clauses, "range", photoField)[0].GetProperty("lte").GetString());
        Assert.Equal("2026-10-05T05:40:00.000Z", Find(clauses, "range", createdField)[0].GetProperty("gte").GetString());
        Assert.Equal("2026-10-05T05:40:00.456Z", Find(clauses, "range", createdField)[0].GetProperty("lte").GetString());
        Assert.Equal("PhotoTime", options.ResponseFields.PhotoTime);
        Assert.Equal("Created", options.ResponseFields.CreatedTime);
        Assert.Equal("Resolution", options.ResponseFields.Resolution);
    }

    [Fact]
    public async Task Mapping_owns_payload_and_maps_confirmed_names_and_configured_optional_metadata()
    {
        var properties = Properties();
        properties["SarMode"] = "strip";
        properties["Leg"] = "leg-7";
        properties["Before"] = "previous";
        properties["After"] = "next";
        var options = Config();
        options.ResponseFields.ModeSar = "SarMode";
        options.ResponseFields.LegId = "Leg";
        options.ResponseFields.PrevOverlayId = "Before";
        options.ResponseFields.NextOverlayId = "After";
        var provider = Provider(_ => Json(Entity(properties)), options);
        var result = await provider.GetAsync("overlay-1", CancellationToken.None);
        Assert.Null(result.Error);
        var overlay = Assert.IsType<ProviderOverlay>(result.Overlay);
        Assert.Equal("overlay-1", overlay.Input.ImageId);
        Assert.Equal("arc", overlay.Input.SensorName);
        Assert.Equal("SAR", overlay.Input.SensorType);
        Assert.Equal("grid", overlay.Input.GridType);
        Assert.Equal("strip", overlay.Input.ModeSar);
        Assert.Equal("https://images.example/1", overlay.Input.ImageUrl);
        Assert.Equal(100, overlay.Input.ImageWidth);
        Assert.Equal(50, overlay.Input.ImageHeight);
        Assert.False(overlay.IsRealtime);
        Assert.Equal("sortie-1", overlay.SortieId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T07:00:00Z"), overlay.Input.PhotoTime);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T08:00:00Z"), overlay.CreatedTime);
        using var document = JsonDocument.Parse(overlay.Payload);
        Assert.Equal("Polygon", document.RootElement.GetProperty("roiFootprint").GetProperty("type").GetString());
        Assert.Equal("leg-7", document.RootElement.GetProperty("legId").GetString());
        Assert.Equal("previous", document.RootElement.GetProperty("prevOverlayId").GetString());
        Assert.Equal("next", document.RootElement.GetProperty("nextOverlayId").GetString());
        Assert.Equal("https://grids.example/1", document.RootElement.GetProperty("gridURI").GetString());
    }

    [Fact]
    public async Task Explicit_timestamp_offset_wins_over_provider_local_zone()
    {
        var properties = Properties();
        properties["PhotoTime"] = "2026-10-19T10:00:00-04:00";
        var result = await Provider(_ => Json(Entity(properties))).GetAsync("overlay-1", CancellationToken.None);
        Assert.Null(result.Error);
        Assert.Equal(DateTimeOffset.Parse("2026-10-19T14:00:00Z"), result.Overlay!.Input.PhotoTime);
    }

    [Fact]
    public async Task Mapping_leaves_optional_contract_fields_for_the_selected_pipeline_to_validate()
    {
        var properties = Properties();
        properties.Remove("ImageWidth");
        properties.Remove("ImageHeight");
        properties.Remove("GridPath");
        properties["Url"] = false;
        var result = await Provider(_ => Json(Entity(properties))).GetAsync("overlay-1", CancellationToken.None);
        Assert.Null(result.Error);
        using var document = JsonDocument.Parse(result.Overlay!.Payload);
        Assert.Equal(JsonValueKind.False, document.RootElement.GetProperty("imageUrl").ValueKind);
        Assert.False(document.RootElement.TryGetProperty("width", out _));
        Assert.False(document.RootElement.TryGetProperty("gridURI", out _));
    }

    [Theory]
    [InlineData("27/03/2026 02:30:00")]
    [InlineData("25/10/2026 01:30:00")]
    [InlineData("not-a-date")]
    public async Task Invalid_or_ambiguous_provider_dates_are_record_errors(string timestamp)
    {
        var properties = Properties(); properties["PhotoTime"] = timestamp;
        var result = await Provider(_ => Json(Entity(properties))).GetAsync("overlay-1", CancellationToken.None);
        Assert.Null(result.Overlay);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task Local_filter_verification_checks_all_fields_inclusive_bounds_false_and_geometry()
    {
        var result = await Provider(_ => Json(Entity())).GetAsync("overlay-1", CancellationToken.None);
        var overlay = Assert.IsType<ProviderOverlay>(result.Overlay);
        var filters = new AutoRunFilters
        {
            SensorNames = ["arc"], SensorTypes = ["SAR"], GridTypes = ["grid"],
            RegistrationQualities = ["Accurate"], SortieIds = ["sortie-1"], IsRealtime = false,
            Resolution = new() { Min = 0.5, Max = 0.5 },
            PhotoTime = new() { From = "2026-10-19T07:00:00Z", To = "2026-10-19T07:00:00Z" },
            CreatedTime = new() { From = "2026-10-19T08:00:00Z", To = "2026-10-19T08:00:00Z" },
            Wkt = "MULTIPOLYGON (((34.5 31.5, 36 31.5, 36 33, 34.5 33, 34.5 31.5)))"
        };
        Assert.True(ProviderQueryBuilder.Matches(overlay, filters));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { IsRealtime = true }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { SensorNames = ["ARC"] }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { SensorTypes = ["EO"] }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { GridTypes = ["other"] }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { RegistrationQualities = ["Sensor"] }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { SortieIds = ["different"] }));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { Resolution = new() { Min = 0.6 } }));
        Assert.False(ProviderQueryBuilder.Matches(overlay with { CreatedTime = null }, filters));
        Assert.False(ProviderQueryBuilder.Matches(overlay with { IsRealtime = null }, filters));
        Assert.False(ProviderQueryBuilder.Matches(overlay, filters with { Wkt = "POLYGON ((1 1, 2 1, 2 2, 1 2, 1 1))" }));
    }

    [Fact]
    public void Invalid_filters_are_rejected_before_the_provider_is_called()
    {
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { SensorNames = [] }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { GridTypes = [" "] }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { Resolution = new() { Min = double.NaN } }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { Resolution = new() { Min = 2, Max = 1 } }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { PhotoTime = new() { From = "2026-10-19T10:00:00" } }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { Wkt = "POINT (1 2)" }));
        Assert.NotEmpty(ProviderQueryBuilder.Validate(new() { Wkt = "not-wkt" }));
        Assert.Empty(ProviderQueryBuilder.Validate(new() { IsRealtime = false }));
    }

    [Fact]
    public async Task Pages_preserve_POST_body_and_invalid_records_and_end_on_empty_entities()
    {
        var bodies = new List<string>();
        var calls = 0;
        var provider = Provider(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType!.MediaType);
            bodies.Add(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
            using var body = JsonDocument.Parse(bodies[^1]);
            var clauses = Clauses(body.RootElement);
            Assert.Equal("ar", Find(clauses, "match", "SENSOR")[0].GetString());
            Assert.Equal(1, Find(clauses, "range", "RESOLUTION")[0].GetProperty("gte").GetDouble());
            Assert.Equal(20, Find(clauses, "range", "RESOLUTION")[0].GetProperty("lte").GetDouble());
            var photoRange = Find(clauses, "range", "date")[0];
            Assert.Equal("2026-10-04T16:40:00.000Z", photoRange.GetProperty("gte").GetString());
            Assert.Equal("2026-10-05T16:43:00.999Z", photoRange.GetProperty("lte").GetString());
            Assert.DoesNotContain("PHOTOTIME", bodies[^1], StringComparison.Ordinal);
            calls++;
            return calls == 1
                ? Json(new { entities_list = new object[] { Entity(), new { properties_list = new { } } }, next_page = "/next?searchAfter=cursor" })
                : Json(new { entities_list = Array.Empty<object>(), next_page = "/next?searchAfter=cursor" });
        });
        var query = Assert.Single(new ProviderQueryBuilder(Options.Create(new ElasticProviderOptions())).Build(new()
        {
            Mode = AutoRunSourceMode.Filters,
            Filters = new()
            {
                SensorNames = ["ar"], Resolution = new() { Min = 1, Max = 20 },
                PhotoTime = new() { From = "2026-10-04T19:40:00+03:00", To = "2026-10-05T16:43:00.999Z" }
            }
        }, []));
        var first = await provider.SearchPageAsync(query, null, CancellationToken.None);
        Assert.Equal(2, first.Items.Count);
        Assert.NotNull(first.Items[0].Overlay);
        Assert.NotNull(first.Items[1].Error);
        Assert.Equal("https://provider.example/next?searchAfter=cursor", first.NextPage);
        var last = await provider.SearchPageAsync(query, first.NextPage, CancellationToken.None);
        Assert.Empty(last.Items);
        Assert.Null(last.NextPage);
        Assert.Equal(bodies[0], bodies[1]);
    }

    [Theory]
    [InlineData("https://other.example/page")]
    [InlineData("http://provider.example/page")]
    [InlineData("https://provider.example:444/page")]
    [InlineData("https://user@provider.example/page")]
    public async Task Continuation_rejects_foreign_origins_and_credentials_before_sending(string continuation)
    {
        var calls = 0;
        var provider = Provider(_ => { calls++; return Json(new { }); });
        await Assert.ThrowsAsync<ElasticProviderException>(() =>
            provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { }), continuation, CancellationToken.None));
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task Malformed_page_is_not_reported_as_an_empty_search()
    {
        var provider = Provider(_ => Json(new { items = Array.Empty<object>() }));
        await Assert.ThrowsAsync<ElasticProviderException>(() =>
            provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { }), null, CancellationToken.None));
    }

    [Fact]
    public async Task Not_found_is_record_result_but_server_failure_is_request_failure()
    {
        var result = await Provider(_ => new(HttpStatusCode.NotFound)).GetAsync("1", CancellationToken.None);
        Assert.Null(result.Overlay); Assert.NotNull(result.Error);
        await Assert.ThrowsAsync<ElasticProviderException>(() =>
            Provider(_ => new(HttpStatusCode.ServiceUnavailable)).GetAsync("1", CancellationToken.None));
    }

    [Fact]
    public async Task Explicit_ID_lookup_rejects_a_different_overlay()
    {
        var result = await Provider(_ => Json(Entity())).GetAsync("another-id", CancellationToken.None);
        Assert.Null(result.Overlay);
        Assert.Contains("different", result.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Declared_response_over_configured_limit_is_rejected_before_reading()
    {
        using var stream = new RepeatingResponseStream(16 * 1024 * 1024 + 1);
        var content = new StreamContent(stream);
        content.Headers.ContentLength = 16 * 1024 * 1024 + 1;

        var options = Config(); options.MaxResponseBytes = 16 * 1024 * 1024;
        var error = await Assert.ThrowsAsync<ProviderResponseLimitException>(() =>
            Provider(_ => new(HttpStatusCode.OK) { Content = content }, options).GetAsync("1", CancellationToken.None));

        Assert.Contains("MaxResponseBytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, stream.BytesRead);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(1L)]
    public async Task Streaming_response_over_configured_limit_is_rejected_even_without_a_reliable_length(long? declaredLength)
    {
        const int responseLength = 16 * 1024 * 1024 + 1;
        using var stream = new RepeatingResponseStream(responseLength);
        var content = new StreamContent(stream);
        content.Headers.ContentLength = declaredLength;

        var options = Config(); options.MaxResponseBytes = 16 * 1024 * 1024;
        var error = await Assert.ThrowsAsync<ProviderResponseLimitException>(() =>
            Provider(_ => new(HttpStatusCode.OK) { Content = content }, options).GetAsync("1", CancellationToken.None));

        Assert.Contains("MaxResponseBytes", error.Message, StringComparison.Ordinal);
        Assert.Equal(responseLength, stream.BytesRead);
    }

    [Fact]
    public void Query_preparation_stops_before_allocating_when_memory_is_under_pressure()
    {
        var memory = new AutoRunMemoryTestGuard { Blocked = true };
        var builder = new ProviderQueryBuilder(Options.Create(Config()), memory);
        Assert.Throws<AutoRunMemoryLimitException>(() => builder.Build(new() { Mode = AutoRunSourceMode.Filters }, []));
    }

    [Fact]
    public async Task Default_response_limit_accepts_valid_pages_larger_than_sixteen_MiB()
    {
        var response = Json(new
        {
            entities_list = new[] { Entity() }, next_page = "",
            padding = new string('x', 16 * 1024 * 1024)
        });
        Assert.True(response.Content.Headers.ContentLength > 16 * 1024 * 1024);
        var result = await Provider(_ => response).SearchPageAsync(
            JsonSerializer.SerializeToElement(new { }), null, CancellationToken.None);
        Assert.Equal("overlay-1", Assert.Single(result.Items).Overlay!.Input.ImageId);
        Assert.Null(result.StoppedReason);
    }

    [Fact]
    public async Task Empty_terminal_page_ignores_unused_foreign_continuation()
    {
        var provider = Provider(_ => Json(new
        {
            entities_list = Array.Empty<object>(), next_page = "https://other.example/unused"
        }));
        var page = await provider.SearchPageAsync(JsonSerializer.SerializeToElement(new { }), null, CancellationToken.None);
        Assert.Empty(page.Items);
        Assert.Null(page.NextPage);
        Assert.Null(page.StoppedReason);
    }

    [Fact]
    public async Task Page_visitor_preserves_accepted_overlays_when_memory_becomes_low_midpage()
    {
        var memory = new AutoRunMemoryTestGuard();
        var properties = Properties(); properties["GlobalId"] = "overlay-2";
        var provider = new ElasticOverlayProvider(new HttpClient(new Handler(_ => Json(new
        {
            entities_list = new[] { Entity(), Entity(properties) }, next_page = "/next"
        }))), Options.Create(Config()), memory);
        var accepted = new List<ProviderOverlay>();
        var page = await provider.VisitPageAsync(JsonSerializer.SerializeToElement(new { }), null, item =>
        {
            accepted.Add(item.Overlay!);
            memory.Blocked = true;
            return true;
        }, CancellationToken.None);

        var first = Assert.Single(accepted);
        Assert.Equal("overlay-1", first.Input.ImageId);
        using var payload = JsonDocument.Parse(first.Payload);
        Assert.Equal("overlay-1", payload.RootElement.GetProperty("id").GetString());
        Assert.Empty(page.Items);
        Assert.Null(page.NextPage);
        Assert.Equal("memoryLimit", page.StoppedReason);
    }

    [Fact]
    public async Task Page_visitor_can_stop_before_mapping_later_records()
    {
        var provider = Provider(_ => Json(new
        {
            entities_list = new object[] { Entity(), new { invalid = "must not be visited" } }, next_page = "/next"
        }));
        var visited = 0;
        var page = await provider.VisitPageAsync(JsonSerializer.SerializeToElement(new { }), null, item =>
        {
            Assert.NotNull(item.Overlay);
            visited++;
            return false;
        }, CancellationToken.None);
        Assert.Equal(1, visited);
        Assert.Empty(page.Items);
        Assert.Null(page.NextPage);
    }

    private static JsonElement[] Clauses(JsonElement query) =>
        query.GetProperty("filter").GetProperty("logical_operators").GetProperty("AND").EnumerateArray().ToArray();
    private static JsonElement Find(IEnumerable<JsonElement> clauses, string operation, string field) =>
        clauses.Single(clause => clause.TryGetProperty(operation, out var op) && op.TryGetProperty(field, out _))
            .GetProperty(operation).GetProperty(field).GetProperty("values");

    private static Dictionary<string, object?> Properties() => new()
    {
        ["GlobalId"] = "overlay-1", ["Url"] = "https://images.example/1", ["GridPath"] = "https://grids.example/1",
        ["Sensor"] = "arc", ["ImagingTechnique"] = "SAR", ["SolutionType"] = "grid", ["Sortie"] = "sortie-1",
        ["RegistrationQuality"] = "Accurate", ["Resolution"] = 0.5, ["ImageWidth"] = 100, ["ImageHeight"] = 50,
        ["PhotoTime"] = "19/10/2026 10:00:00", ["Created"] = "19/10/2026 11:00:00", ["IsRealTime"] = false
    };
    private static object Entity(Dictionary<string, object?>? properties = null) => new
    {
        exclusive_id = new { entity_id = "overlay-1" }, geo = new { wkt = Area }, properties_list = properties ?? Properties()
    };
    private static ElasticProviderOptions Config() => new() { BaseUrl = "https://provider.example" };
    private static ElasticOverlayProvider Provider(Func<HttpRequestMessage, HttpResponseMessage> response, ElasticProviderOptions? options = null) =>
        new(new HttpClient(new Handler(response)), Options.Create(options ?? Config()));
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }

    // Produces a large non-seekable response without allocating a second large payload in the test.
    private sealed class RepeatingResponseStream(int size) : Stream
    {
        public int BytesRead { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => BytesRead; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = Math.Min(count, size - BytesRead);
            buffer.AsSpan(offset, read).Fill((byte)' ');
            BytesRead += read;
            return read;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = Math.Min(buffer.Length, size - BytesRead);
            buffer.Span[..read].Fill((byte)' ');
            BytesRead += read;
            return ValueTask.FromResult(read);
        }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
