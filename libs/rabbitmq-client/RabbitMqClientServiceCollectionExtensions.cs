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
        var rabbitSection = configuration.GetSection(RabbitMqClientOptions.SectionName);

        services.AddOptions<RabbitMqFlowControlOptions>()
            .Bind(flowControlSection)
            .PostConfigure<IOptions<RabbitMqClientOptions>>((flowControl, rabbitOptions) =>
            {
                var rabbit = rabbitOptions.Value;
                if (string.Equals(flowControl.Host, "localhost", StringComparison.Ordinal) &&
                    !flowControlSection.GetSection("Host").Exists())
                {
                    flowControl.Host = rabbit.Host;
                }

                if (string.Equals(flowControl.Username, "guest", StringComparison.Ordinal) &&
                    !flowControlSection.GetSection("Username").Exists())
                {
                    flowControl.Username = rabbit.Username;
                }

                if (string.Equals(flowControl.Password, "guest", StringComparison.Ordinal) &&
                    !flowControlSection.GetSection("Password").Exists())
                {
                    flowControl.Password = rabbit.Password;
                }

                if (string.Equals(flowControl.VirtualHost, "/", StringComparison.Ordinal) &&
                    !flowControlSection.GetSection("VirtualHost").Exists())
                {
                    flowControl.VirtualHost = rabbit.VirtualHost;
                }
            })
            .Validate(options => options.IsValid(out _), "RabbitMq FlowControl configuration is invalid.")
            .ValidateOnStart();

        services.AddSingleton<RabbitMqFlowControl>();
        services.AddSingleton<IRabbitMqFlowControl>(sp => sp.GetRequiredService<RabbitMqFlowControl>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<RabbitMqFlowControl>());
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
