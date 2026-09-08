using ImagingPipeline.Observability;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
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
        RabbitMqNativeTracing.Configure();

        AddRabbitMqOptions(services, configuration)
            .Validate(options => options.IsPublisherValid(out _), "RabbitMq publisher configuration is invalid.")
            .ValidateOnStart();

        AddFlowControl(services, configuration);

        services.TryAddSingleton<IRabbitMqPublisherConnectionManager, RabbitMqPublisherConnectionManager>();
        services.TryAddSingleton<IRabbitMqPublisherChannelPool, RabbitMqPublisherChannelPool>();
        services.TryAddSingleton<IMessageTraceContextPropagator>(RabbitMqNativeTracing.Propagator);
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

        if (HasInputCluster(configuration))
        {
            services.TryAddSingleton<IRabbitMqInputClusterConnectionManager, RabbitMqInputClusterConnectionManager>();
            services.TryAddSingleton<IRabbitMqInputClusterChannelPool, RabbitMqInputClusterChannelPool>();
        }

        return services;
    }

    private static bool HasInputCluster(IConfiguration configuration) =>
        configuration
            .GetSection(RabbitMqClientOptions.SectionName)
            .GetSection(nameof(RabbitMqClientOptions.InputCluster))
            .Exists();

    public static IServiceCollection AddRabbitMqClient(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        return services.AddRabbitMqConsumer(configuration);
    }

    private static void AddFlowControl(
        IServiceCollection services,
        IConfiguration configuration)
    {
        if (services.Any(d => d.ServiceType == typeof(IRabbitMqFlowControl)))
        {
            return;
        }

        var flowControlSection = configuration.GetSection(RabbitMqFlowControlOptions.SectionName);

        services.AddOptions<RabbitMqFlowControlOptions>()
            .Bind(flowControlSection)
            .PostConfigure<IOptions<RabbitMqClientOptions>>((flowControl, rabbitOptions) =>
            {
                var rabbit = rabbitOptions.Value;
                InheritIfMissing(flowControlSection, "Host", rabbit.Host, v => flowControl.Host = v);
                InheritIfMissing(flowControlSection, "Username", rabbit.Username, v => flowControl.Username = v);
                InheritIfMissing(flowControlSection, "Password", rabbit.Password, v => flowControl.Password = v);
                InheritIfMissing(flowControlSection, "VirtualHost", rabbit.VirtualHost, v => flowControl.VirtualHost = v);
            })
            .Validate(options => options.IsValid(out _), "RabbitMq FlowControl configuration is invalid.")
            .ValidateOnStart();

        services.AddSingleton<RabbitMqFlowControl>();
        services.AddSingleton<IRabbitMqFlowControl>(sp => sp.GetRequiredService<RabbitMqFlowControl>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RabbitMqFlowControl>());
    }

    private static void InheritIfMissing(
        IConfigurationSection section,
        string key,
        string fallback,
        Action<string> apply)
    {
        if (!section.GetSection(key).Exists())
        {
            apply(fallback);
        }
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
