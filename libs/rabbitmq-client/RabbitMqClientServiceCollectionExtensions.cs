using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient;

public static class RabbitMqClientServiceCollectionExtensions
{
    private sealed class RabbitMqOptionsBindingMarker
    {
    }

    public static IServiceCollection AddRabbitMqPublisher(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        AddRabbitMqOptions(services, configuration)
            .Validate(options => options.IsPublisherValid(out _), "RabbitMq publisher configuration is invalid.")
            .ValidateOnStart();

        services.TryAddSingleton<IRabbitMqPublisherConnectionManager, RabbitMqPublisherConnectionManager>();
        services.TryAddSingleton<IRabbitMqPublisherChannelPool, RabbitMqPublisherChannelPool>();
        services.TryAddSingleton<IRabbitMqPublisher, RabbitMqPublisher>();
        return services;
    }

    public static IServiceCollection AddRabbitMqConsumer(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddRabbitMqPublisher(configuration);
        AddRabbitMqOptions(services, configuration)
            .Validate(options => options.IsConsumerValid(out _), "RabbitMq consumer configuration is invalid.")
            .ValidateOnStart();

        services.TryAddSingleton<IRabbitMqConsumerConnectionManager, RabbitMqConsumerConnectionManager>();
        services.TryAddSingleton<RabbitMqOutcomeRouter>();
        services.TryAddSingleton<IRabbitMqConsumer, RabbitMqConsumer>();
        services.TryAddSingleton<IRabbitMqClient, RabbitMqClient>();
        return services;
    }

    public static IServiceCollection AddRabbitMqClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddRabbitMqConsumer(configuration);
    }

    private static OptionsBuilder<RabbitMqClientOptions> AddRabbitMqOptions(
        IServiceCollection services,
        IConfiguration configuration)
    {
        var options = services.AddOptions<RabbitMqClientOptions>();
        if (services.Any(descriptor => descriptor.ServiceType == typeof(RabbitMqOptionsBindingMarker)))
        {
            return options;
        }

        services.AddSingleton(new RabbitMqOptionsBindingMarker());
        return options.Bind(configuration.GetSection(RabbitMqClientOptions.SectionName));
    }
}
