using System.Net;
using System.Text;
using System.Text.Json;
using ImagingPipeline.RuleEngine.Input;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderSensorTypeTests
{
    [Fact]
    public async Task Raw_lookup_uses_same_entity_path_and_only_replaces_normalized_sensor_type()
    {
        Uri? sent = null;
        using var client = new HttpClient(new Handler(request =>
        {
            sent = request.RequestUri;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Null(request.Content);
            Assert.Equal("token", request.Headers.GetValues("X-Key").Single());
            return Json(new { id = "id /one", sensorType = "PAN_MSI", imageUrl = "must-not-replace", width = -100 });
        }));
        var overlay = Overlay("id /one");
        var before = overlay.Payload.ToArray();
        var options = Config(); options.Headers["X-Key"] = "token"; options.To = 75;
        var provider = new ElasticOverlayProvider(client, Options.Create(options));

        var result = await provider.ResolveSensorTypeAsync(overlay, CancellationToken.None);

        Assert.Null(result.Error);
        Assert.NotNull(result.Overlay);
        Assert.Equal("/ErisImages/v2.0/ErisImages/Images/TableEntities/id%20%2Fone", sent!.AbsolutePath);
        Assert.Equal("?to=75&result_type=4", sent.Query);
        Assert.Equal("PAN_MSI", result.Overlay.Input.SensorType);
        Assert.Equal(overlay.Input with { SensorType = "PAN_MSI" }, result.Overlay.Input);
        Assert.Equal(before, overlay.Payload);
        using var original = JsonDocument.Parse(before);
        using var changed = JsonDocument.Parse(result.Overlay.Payload);
        foreach (var property in original.RootElement.EnumerateObject().Where(p => !p.NameEquals("sensorType")))
            Assert.Equal(property.Value.GetRawText(), changed.RootElement.GetProperty(property.Name).GetRawText());
        Assert.Equal("PAN_MSI", changed.RootElement.GetProperty("sensorType").GetString());
        Assert.Equal(overlay.CreatedTime, result.Overlay.CreatedTime);
        Assert.Equal(overlay.SortieId, result.Overlay.SortieId);
        Assert.Equal(overlay.IsRealtime, result.Overlay.IsRealtime);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("not-json")]
    [InlineData("{\"id\":\"different\",\"sensorType\":\"EO\"}")]
    [InlineData("{\"id\":\"one\",\"sensorType\":null}")]
    [InlineData("{\"id\":\"one\",\"sensorType\":12}")]
    [InlineData("{\"id\":\"one\",\"sensorType\":\" \"}")]
    [InlineData("{\"id\":\"one\",\"sensorType\":\"EO\",\"sensorType\":\"SAR\"}")]
    public async Task Invalid_raw_schema_returns_an_invalid_record_without_type_fallback(string raw)
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.OK) { Content = new StringContent(raw) }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var result = await provider.ResolveSensorTypeAsync(Overlay("one"), CancellationToken.None);
        Assert.Null(result.Overlay);
        Assert.NotNull(result.Error);
        Assert.Contains("sensorType", result.Error);
    }

    [Fact]
    public async Task Missing_raw_overlay_is_an_invalid_record()
    {
        using var client = new HttpClient(new Handler(_ => new(HttpStatusCode.NotFound)));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var result = await provider.ResolveSensorTypeAsync(Overlay("one"), CancellationToken.None);
        Assert.Null(result.Overlay);
        Assert.Contains("not found", result.Error);
    }

    [Fact]
    public async Task Page_visitor_can_await_raw_lookup_after_search_request_releases_serial_gate()
    {
        var requests = new List<(HttpMethod Method, string Query)>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add((request.Method, request.RequestUri!.Query));
            return request.Method == HttpMethod.Post ? Json(new
            {
                entities_list = new[] { new
                {
                    geo = new { wkt = "POLYGON ((0 0, 1 0, 1 1, 0 1, 0 0))" },
                    properties_list = new
                    {
                        GlobalId = "one", Sensor = "camera", ImagingTechnique = "old-type", SolutionType = "grid",
                        RegistrationQuality = "Accurate", Resolution = 1, PhotoTime = "2026-10-26T10:00:00Z"
                    }
                } },
                next_page = "/next?searchAfter=opaque"
            }) : Json(new { id = "one", sensorType = "SWIR" });
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(Config()));
        var visited = 0;
        var page = await provider.VisitPageAsync(JsonSerializer.SerializeToElement(new { }), null, async item =>
        {
            var resolved = await provider.ResolveSensorTypeAsync(item.Overlay!, CancellationToken.None);
            Assert.Equal("SWIR", resolved.Overlay!.Input.SensorType);
            visited++;
            return true;
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, visited);
        Assert.Empty(page.Items);
        Assert.NotNull(page.NextPage);
        Assert.Equal([(HttpMethod.Post, "?to=1000"), (HttpMethod.Get, "?to=1000&result_type=4")], requests);
    }

    private static ProviderOverlay Overlay(string id)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            id, sensorName = "camera", sensorType = "old-type", registrationQuality = "Accurate",
            bestResolution = 1, photoTime = "2026-10-26T10:00:00Z", gridType = "grid", imageUrl = "https://images.example/a",
            width = 40, height = 50, gridURI = "https://grid.example/a",
            roiFootprint = new { type = "Polygon", coordinates = new[] { new[] { new[] { 0, 0 }, [1, 0], [1, 1], [0, 1], [0, 0] } } },
            extension = new { preserved = true }
        });
        return new(payload, GatewayInputMessageParser.Parse(payload), DateTimeOffset.UtcNow, false, "sortie");
    }
    private static ElasticProviderOptions Config() => new()
    { BaseUrl = "https://provider.example", RequestDelayMs = 0, MaxRetries = 0 };
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(response(request));
    }
}
