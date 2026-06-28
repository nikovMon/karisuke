using ImagingPipeline.ElasticsearchClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nest;

namespace ImagingPipeline.ElasticsearchClient.Tests;

public sealed class ElasticsearchExtensionsTests
{
    [Fact]
    public void AddElasticsearchClientRegistersRawAndGenericClients()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "http://localhost:9200",
            ["Elasticsearch:DefaultIndex"] = "rules"
        });

        Assert.NotNull(provider.GetRequiredService<IElasticClient>());
        Assert.IsType<ElasticsearchDocumentClient>(provider.GetRequiredService<IElasticsearchDocumentClient>());
    }

    [Fact]
    public void AddElasticsearchClientBindsOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "https://elastic.example",
            ["Elasticsearch:DefaultIndex"] = "rules",
            ["Elasticsearch:Username"] = "user",
            ["Elasticsearch:Password"] = "password"
        });

        var options = provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value;

        Assert.Equal("https://elastic.example", options.Uri);
        Assert.Equal("rules", options.DefaultIndex);
        Assert.Equal("user", options.Username);
        Assert.Equal("password", options.Password);
    }

    [Fact]
    public void AddElasticsearchClientRejectsInvalidUriWhenOptionsAreResolved()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "ftp://localhost:9200",
            ["Elasticsearch:DefaultIndex"] = "rules"
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value);
    }

    [Fact]
    public void AddElasticsearchClientRejectsMissingDefaultIndexWhenOptionsAreResolved()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "http://localhost:9200",
            ["Elasticsearch:DefaultIndex"] = ""
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value);
    }

    [Fact]
    public void AddElasticsearchClientRejectsPartialCredentialsWhenOptionsAreResolved()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "http://localhost:9200",
            ["Elasticsearch:DefaultIndex"] = "rules",
            ["Elasticsearch:Username"] = "user"
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value);
    }

    private static ServiceProvider BuildProvider(Dictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddElasticsearchClient(configuration);
        return services.BuildServiceProvider(validateScopes: true);
    }
}
