using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ImagingPipeline.RabbitClient;

public static class RabbitClientServiceCollectionExtensions
{
    public static IServiceCollection AddRabbitPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<RabbitClientOptions>()
            .Bind(configuration.GetSection(RabbitClientOptions.SectionName))
            .Validate(options => options.IsPublisherValid(out _), "RabbitMq publisher configuration is invalid.")
            .ValidateOnStart();

        services.TryAddSingleton<IRabbitConnectionManager, RabbitConnectionManager>();
        services.TryAddSingleton<IRabbitPublisherChannelPool, RabbitPublisherChannelPool>();
        services.TryAddSingleton<IRabbitPublisher, RabbitPublisher>();
        return services;
    }

    public static IServiceCollection AddRabbitConsumer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRabbitPublisher(configuration);
        services.AddOptions<RabbitClientOptions>()
            .Bind(configuration.GetSection(RabbitClientOptions.SectionName))
            .Validate(options => options.IsConsumerValid(out _), "RabbitMq consumer configuration is invalid.")
            .ValidateOnStart();

        services.TryAddSingleton<RabbitOutcomeRouter>();
        services.TryAddSingleton<IRabbitConsumer, RabbitConsumer>();
        services.TryAddSingleton<IRabbitBatchConsumer, RabbitBatchConsumer>();
        services.TryAddSingleton<IRabbitClient, RabbitClient>();
        return services;
    }

    public static IServiceCollection AddRabbitClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddRabbitConsumer(configuration);
    }
}
