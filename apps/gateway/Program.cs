namespace ImagingPipeline.Gateway;

using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using ImagingPipeline.Gateway.Processing.Messages;
using ImagingPipeline.Gateway.Processing.Rules;
using ImagingPipeline.Observability;
using ImagingPipeline.RabbitMqClient;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddImagingPipelineObservability(ObservabilityServiceNames.Gateway);

        var shutdownTimeoutSeconds = 30;
        var configuredShutdownTimeout = builder.Configuration["Gateway:ShutdownTimeoutSeconds"];
        if (int.TryParse(configuredShutdownTimeout, out var parsedShutdownTimeout) && parsedShutdownTimeout > 0)
        {
            shutdownTimeoutSeconds = parsedShutdownTimeout;
        }

        builder.Services.Configure<HostOptions>(options =>
        {
            options.ShutdownTimeout = TimeSpan.FromSeconds(shutdownTimeoutSeconds);
        });

        builder.Services.AddOptions<GatewaySettings>()
            .Bind(builder.Configuration.GetSection(GatewaySettings.SectionName))
            .Validate(options => options.IsValid(out _), "Gateway configuration is invalid.")
            .ValidateOnStart();

        builder.Services.AddElasticsearchClient(builder.Configuration);
        builder.Services.AddRabbitMqClient(builder.Configuration);

        builder.Services.AddSingleton<GatewayHealthState>();
        builder.Services.AddSingleton<JsonPathReader>();
        builder.Services.AddSingleton<GatewayGeometryConverter>();
        builder.Services.AddSingleton<GatewayInputMessageParser>();
        builder.Services.AddSingleton<GatewayOutputMessageBuilder>();
        builder.Services.AddSingleton<RuleMatcher>();
        builder.Services.AddSingleton<IRuleRepository, ElasticsearchRuleRepository>();
        builder.Services.AddSingleton<ActiveRuleCache>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<ActiveRuleCache>());
        builder.Services.AddSingleton<GatewayWorker>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<GatewayWorker>());

        await builder.Build().RunAsync();
    }
}
