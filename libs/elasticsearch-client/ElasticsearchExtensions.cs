using Elasticsearch.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Nest;

namespace ImagingPipeline.ElasticsearchClient;

public static class ElasticsearchExtensions
{
    public static IServiceCollection AddElasticsearchClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<ElasticsearchClientOptions>()
            .Bind(configuration.GetSection(ElasticsearchClientOptions.SectionName))
            .Validate(options => options.IsValid(out _), "Elasticsearch configuration is invalid.")
            .ValidateOnStart();

        services.AddSingleton<IElasticClient>(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ElasticsearchClientOptions>>().Value;
            var pool = new SingleNodeConnectionPool(new Uri(options.Uri));
            var settings = new ConnectionSettings(
                    pool,
                    sourceSerializer: (_, _) => new SystemTextJsonSourceSerializer())
                .DefaultIndex(options.DefaultIndex)
                .DisableDirectStreaming();

            if (!string.IsNullOrEmpty(options.Username) && !string.IsNullOrEmpty(options.Password))
            {
                settings = settings.BasicAuthentication(options.Username, options.Password);
            }

            return new ElasticClient(settings);
        });
        services.AddSingleton<IElasticsearchDocumentClient, ElasticsearchDocumentClient>();

        return services;
    }
}
