using System.Net;
using System.Text;
using System.Text.Json;
using ImagingPipeline.Rules.Api.AutoRuns;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using ImagingPipeline.Rules.Api.Tests.Fakes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.Tests;

public sealed class AutoRunProviderHeaderTests
{
    private const string Secret = "fake-secret-canary==";

    [Fact]
    public async Task Configured_headers_reach_GET_POST_and_continuation_with_exact_values()
    {
        const string exactValue = "  unchanged\tvalue==  ";
        var calls = new List<(HttpMethod Method, Uri Uri, string? Body)>();
        using var configuration = Configuration(new()
        {
            ["AutoRun:Provider:Headers:Authorization"] = "Bearer " + Secret,
            ["AutoRun:Provider:Headers:X-Api-Key"] = exactValue
        });
        using var services = ProviderServices(configuration, async request =>
        {
            Assert.Equal("Bearer " + Secret, Assert.Single(request.Headers.NonValidated["authorization"]));
            Assert.Equal(exactValue, Assert.Single(request.Headers.NonValidated["x-api-key"]));
            calls.Add((request.Method, request.RequestUri!, request.Content is null ? null : await request.Content.ReadAsStringAsync()));
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.NotFound);
            Assert.Equal("application/json-patch+json", request.Content!.Headers.ContentType!.MediaType);
            return calls.Count == 2
                ? Json(new { entities_list = new object[] { new { } }, next_page = "/next?searchAfter=cursor" })
                : Json(new { entities_list = Array.Empty<object>() });
        });
        var provider = services.GetRequiredService<IElasticOverlayProvider>();
        var query = JsonSerializer.SerializeToElement(new { filter = new { } });

        await provider.GetAsync("overlay-1", CancellationToken.None);
        var first = await provider.SearchPageAsync(query, null, CancellationToken.None);
        await provider.SearchPageAsync(query, first.NextPage, CancellationToken.None);

        Assert.Equal(3, calls.Count);
        Assert.Equal(HttpMethod.Get, calls[0].Method);
        Assert.EndsWith("/TableEntities/overlay-1", calls[0].Uri.AbsolutePath);
        Assert.Null(calls[0].Body);
        Assert.Equal("", calls[1].Uri.Query);
        Assert.Equal("https://provider.example/next?searchAfter=cursor", calls[2].Uri.AbsoluteUri);
        Assert.Equal(calls[1].Body, calls[2].Body);
    }

    [Fact]
    public async Task Omitted_headers_keep_unauthenticated_requests_unchanged()
    {
        using var configuration = Configuration();
        var calls = 0;
        using var services = ProviderServices(configuration, request =>
        {
            calls++;
            Assert.Empty(request.Headers);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        services.GetRequiredService<IStartupValidator>().Validate();
        Assert.Empty(services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.Headers);
        await services.GetRequiredService<IElasticOverlayProvider>().GetAsync("1", CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("https://other.example/next")]
    [InlineData("http://provider.example/next")]
    [InlineData("https://provider.example:444/next")]
    [InlineData("https://user@provider.example/next")]
    public async Task Configured_credentials_are_not_sent_to_an_invalid_continuation(string next)
    {
        using var configuration = Configuration(new() { ["AutoRun:Provider:Headers:Authorization"] = "Bearer " + Secret });
        var destinations = new List<Uri>();
        using var services = ProviderServices(configuration, request =>
        {
            destinations.Add(request.RequestUri!);
            Assert.Equal("Bearer " + Secret, Assert.Single(request.Headers.NonValidated["Authorization"]));
            return Task.FromResult(Json(new { entities_list = new object[] { new { } }, next_page = next }));
        });
        var provider = services.GetRequiredService<IElasticOverlayProvider>();
        var query = JsonSerializer.SerializeToElement(new { });

        await Assert.ThrowsAsync<ElasticProviderException>(() => provider.SearchPageAsync(query, null, CancellationToken.None));
        await Assert.ThrowsAsync<ElasticProviderException>(() => provider.SearchPageAsync(query, next, CancellationToken.None));

        Assert.Equal("provider.example", Assert.Single(destinations).Host);
    }

    [Fact]
    public void Production_handler_keeps_redirects_disabled_with_configured_credentials()
    {
        using var configuration = Configuration(new() { ["AutoRun:Provider:Headers:Authorization"] = "Bearer " + Secret });
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        using var provider = services.BuildServiceProvider();
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(nameof(IElasticOverlayProvider));
        while (handler is DelegatingHandler wrapper) handler = wrapper.InnerHandler!;
        Assert.False(Assert.IsType<HttpClientHandler>(handler).AllowAutoRedirect);
    }

    [Theory]
    [InlineData("", Secret)]
    [InlineData("Bad Header", Secret)]
    [InlineData("Bad:Header", Secret)]
    [InlineData("Bad\r\nHeader", Secret)]
    [InlineData("Héader", Secret)]
    [InlineData("X-Key", null)]
    [InlineData("X-Key", Secret + "\r\nInjected: true")]
    [InlineData("X-Key", Secret + "\0")]
    [InlineData("X-Key", Secret + "\u007f")]
    [InlineData("X-Key", Secret + "\u0085")]
    [InlineData("Host", Secret)]
    [InlineData("content-type", Secret)]
    [InlineData("Content-Length", Secret)]
    [InlineData("Content-Encoding", Secret)]
    [InlineData("Connection", Secret)]
    [InlineData("Transfer-Encoding", Secret)]
    [InlineData("TE", Secret)]
    [InlineData("Trailer", Secret)]
    [InlineData("Upgrade", Secret)]
    [InlineData("Keep-Alive", Secret)]
    [InlineData("Proxy-Connection", Secret)]
    [InlineData("Proxy-Authenticate", Secret)]
    [InlineData("Proxy-Authorization", Secret)]
    [InlineData("Expect", Secret)]
    public void Invalid_headers_fail_startup_without_exposing_values(string name, string? value)
    {
        using var configuration = Configuration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAutoRuns(configuration);
        services.Configure<ElasticProviderOptions>(options => options.Headers = new(StringComparer.Ordinal)
        {
            [name] = value!
        });
        using var provider = services.BuildServiceProvider();

        var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());

        Assert.Contains("headers", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Case_variant_duplicates_and_null_dictionary_fail_startup()
    {
        foreach (var headers in new Dictionary<string, string>?[]
        {
            new(StringComparer.Ordinal) { ["Authorization"] = Secret, ["authorization"] = "other-" + Secret },
            null
        })
        {
            using var configuration = Configuration();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddAutoRuns(configuration);
            services.Configure<ElasticProviderOptions>(options => options.Headers = headers!);
            using var provider = services.BuildServiceProvider();
            var error = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{\"nested\":\"fake-secret-canary==\"}")]
    [InlineData("[\"fake-secret-canary==\"]")]
    public void Invalid_header_configuration_shapes_fail_startup(string configuredValue)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes("{\"AutoRun\":{\"Provider\":{\"Headers\":{\"X-Key\":" + configuredValue + "}}}}"));
        using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddJsonStream(source).Build();
        using var services = ProviderServices(configuration, _ => throw new InvalidOperationException("No request should be sent."));

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Scalar_header_dictionary_configuration_fails_without_exposing_its_value()
    {
        using var configuration = Configuration(new() { ["AutoRun:Provider:Headers"] = Secret });
        using var services = ProviderServices(configuration, _ => throw new InvalidOperationException("No request should be sent."));

        var error = Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());

        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Environment_override_binds_case_insensitively_and_preserves_header_value()
    {
        var prefix = $"AutoRunHeadersTest_{Guid.NewGuid():N}_";
        var variable = prefix + "AutoRun__Provider__Headers__x-api-key";
        const string value = "  exact environment value==  ";
        Environment.SetEnvironmentVariable(variable, value);
        try
        {
            using var configuration = (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AutoRun:Provider:BaseUrl"] = "https://provider.example",
                ["AutoRun:Provider:Headers:X-Api-Key"] = "overridden"
            }).AddEnvironmentVariables(prefix).Build();
            using var services = ProviderServices(configuration, request =>
            {
                Assert.Equal(value, Assert.Single(request.Headers.NonValidated["X-API-KEY"]));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            });
            services.GetRequiredService<IStartupValidator>().Validate();
            var headers = services.GetRequiredService<IOptions<ElasticProviderOptions>>().Value.Headers;
            Assert.Single(headers);
            Assert.Equal(value, headers["X-API-KEY"]);
            await services.GetRequiredService<IElasticOverlayProvider>().GetAsync("1", CancellationToken.None);
        }
        finally { Environment.SetEnvironmentVariable(variable, null); }
    }

    [Fact]
    public async Task Http_client_trace_logs_redact_configured_and_response_header_values()
    {
        using var logs = new RecordingLoggerProvider();
        using var configuration = Configuration(new()
        {
            ["AutoRun:Provider:Headers:Authorization"] = "Bearer " + Secret,
            ["AutoRun:Provider:Headers:X-Api-Key"] = Secret
        });
        using var services = ProviderServices(configuration, request =>
        {
            Assert.Equal(Secret, Assert.Single(request.Headers.NonValidated["X-Api-Key"]));
            var response = new HttpResponseMessage(HttpStatusCode.NotFound);
            response.Headers.Add("X-Response-Key", "response-" + Secret);
            return Task.FromResult(response);
        }, logs);
        await services.GetRequiredService<IElasticOverlayProvider>().GetAsync("1", CancellationToken.None);

        var entries = logs.Entries.Where(entry => entry.Category.StartsWith("System.Net.Http.HttpClient", StringComparison.Ordinal)).ToArray();
        Assert.Contains(entries, entry => entry.Message.Contains("Authorization", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("X-Api-Key", StringComparison.Ordinal));
        Assert.Contains(entries, entry => entry.Message.Contains("X-Response-Key", StringComparison.Ordinal));
        Assert.All(entries, entry =>
        {
            Assert.DoesNotContain(Secret, entry.Message, StringComparison.Ordinal);
            Assert.DoesNotContain(Secret, entry.Exception?.ToString() ?? "", StringComparison.Ordinal);
            Assert.All(entry.Properties.Values, value => Assert.DoesNotContain(Secret, value?.ToString() ?? "", StringComparison.Ordinal));
        });
    }

    private static ConfigurationRoot Configuration(Dictionary<string, string?>? values = null)
    {
        values ??= [];
        values.TryAdd("AutoRun:Provider:BaseUrl", "https://provider.example");
        return (ConfigurationRoot)new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static ServiceProvider ProviderServices(IConfiguration configuration,
        Func<HttpRequestMessage, Task<HttpResponseMessage>> response, RecordingLoggerProvider? logs = null)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            if (logs is not null) builder.AddProvider(logs);
        });
        services.AddAutoRuns(configuration);
        services.AddHttpClient<IElasticOverlayProvider, ElasticOverlayProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new Handler(response));
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => response(request);
    }
}
