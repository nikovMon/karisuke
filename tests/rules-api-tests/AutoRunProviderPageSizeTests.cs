using System.Net;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderPageSizeTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1000)]
    [InlineData(10000)]
    public async Task Configured_to_reaches_lookup_first_page_and_continuation_without_changing_cursor_or_body(int to)
    {
        const string cursor = "searchAfter=%5B1111%2C%22a%2Bb%26c%3Dd%22%5D";
        var requests = new List<(HttpMethod Method, Uri Uri, string? Body)>();
        using var client = new HttpClient(new Handler(async request =>
        {
            requests.Add((request.Method, request.RequestUri!,
                request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            return request.Method == HttpMethod.Get ? new(HttpStatusCode.NotFound) : new(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new
                {
                    entities_list = requests.Count == 2 ? new object[] { new { } } : [],
                    next_page = "/next?sendingSystem=one%20two&" + cursor + "&to=9&TO=8&%74o=7&geo_type=Wkt"
                }), Encoding.UTF8, "application/json")
            };
        }));
        var provider = new ElasticOverlayProvider(client, Options.Create(new ElasticProviderOptions
        {
            BaseUrl = "https://provider.example", To = to
        }));
        var query = JsonSerializer.SerializeToElement(new { filter = new { }, sort = new { date = "desc" } });

        await provider.GetAsync("id-with-space a", CancellationToken.None);
        var first = await provider.SearchPageAsync(query, null, CancellationToken.None);
        var last = await provider.SearchPageAsync(query, first.NextPage, CancellationToken.None);

        Assert.Equal(3, requests.Count);
        Assert.EndsWith("/TableEntities/id-with-space%20a", requests[0].Uri.AbsolutePath);
        Assert.Equal($"?to={to}", requests[0].Uri.Query);
        Assert.Equal($"?to={to}", requests[1].Uri.Query);
        Assert.Equal("?sendingSystem=one%20two&" + cursor + $"&geo_type=Wkt&to={to}", requests[2].Uri.Query);
        Assert.Equal(first.NextPage, requests[2].Uri.AbsoluteUri);
        Assert.Equal(requests[1].Body, requests[2].Body);
        Assert.Equal(query.GetRawText(), requests[1].Body);
        Assert.Empty(last.Items);
        Assert.Null(last.NextPage);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10001)]
    public async Task Invalid_to_fails_before_any_provider_request(int to)
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("Must not send.")));
        var provider = new ElasticOverlayProvider(client, Options.Create(new ElasticProviderOptions
        {
            BaseUrl = "https://provider.example", To = to
        }));
        var query = JsonSerializer.SerializeToElement(new { });
        await Assert.ThrowsAsync<ElasticProviderException>(() => provider.GetAsync("id", CancellationToken.None));
        await Assert.ThrowsAsync<ElasticProviderException>(() => provider.SearchPageAsync(query, null, CancellationToken.None));
        await Assert.ThrowsAsync<ElasticProviderException>(() => provider.SearchPageAsync(query, "/next?to=1", CancellationToken.None));
    }

    [Theory]
    [InlineData(null, 1000, true)]
    [InlineData("0", 0, true)]
    [InlineData("10000", 10000, true)]
    [InlineData("-1", -1, false)]
    [InlineData("10001", 10001, false)]
    public void Configuration_binds_to_and_validates_bounds_on_startup(string? configured, int expected, bool valid)
    {
        var values = new Dictionary<string, string?>();
        if (configured is not null) values["AutoRun:Provider:To"] = configured;
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        using var provider = services.BuildServiceProvider();
        if (valid)
        {
            provider.GetRequiredService<IStartupValidator>().Validate();
            Assert.Equal(expected, provider.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.To);
        }
        else
        {
            var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
            Assert.Contains("To must be between 0 and 10000", error.Message);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
