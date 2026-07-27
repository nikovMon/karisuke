using Elasticsearch.Net;
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
            ["Elasticsearch:Index"] = "rules"
        });

        Assert.NotNull(provider.GetRequiredService<IElasticClient>());
        var documentClient = Assert.IsType<ElasticsearchDocumentClient>(
            provider.GetRequiredService<IElasticsearchDocumentClient>());
        Assert.Same(
            documentClient,
            provider.GetRequiredService<IElasticsearchPointInTimeClient>());
    }

    [Fact]
    public void AddElasticsearchClientBindsOptions()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "https://elastic.example",
            ["Elasticsearch:Index"] = "rules",
            ["Elasticsearch:Username"] = "user",
            ["Elasticsearch:Password"] = "password",
            ["Elasticsearch:TimeoutSeconds"] = "45"
        });

        var options = provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value;

        Assert.Equal("https://elastic.example", options.Uri);
        Assert.Equal("rules", options.Index);
        Assert.Equal("user", options.Username);
        Assert.Equal("password", options.Password);
        Assert.Equal(45, options.TimeoutSeconds);
        var clientSettings = (IConnectionConfigurationValues)provider
            .GetRequiredService<IElasticClient>()
            .ConnectionSettings;
        Assert.Equal("user", clientSettings.BasicAuthenticationCredentials?.Username);
        Assert.True(clientSettings.EnableApiVersioningHeader);
    }

    [Fact]
    public void AddElasticsearchClientRejectsInvalidUriWhenOptionsAreResolved()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "ftp://localhost:9200",
            ["Elasticsearch:Index"] = "rules"
        });

        Assert.Throws<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value);
    }

    [Fact]
    public void AddElasticsearchClientRejectsMissingIndexWhenOptionsAreResolved()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Elasticsearch:Uri"] = "http://localhost:9200",
            ["Elasticsearch:Index"] = ""
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
            ["Elasticsearch:Index"] = "rules",
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
