namespace ImagingPipeline.Gateway;

using Elastic.Clients.Elasticsearch;
using Elastic.Transport;
using ImagingPipeline.Gateway.Application.Messages;
using ImagingPipeline.Gateway.Application.RabbitMq;
using ImagingPipeline.Gateway.Application.Rules;
using ImagingPipeline.Gateway.Configuration;
using ImagingPipeline.Gateway.Health;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public static class Program
{
    public static async Task Main(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();

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

        builder.Services.AddOptions<RabbitMqSettings>()
            .Bind(builder.Configuration.GetSection(RabbitMqSettings.SectionName))
            .Validate(options => options.IsValid(out _), "RabbitMq configuration is invalid.")
            .ValidateOnStart();

        builder.Services.AddOptions<ElasticsearchSettings>()
            .Bind(builder.Configuration.GetSection(ElasticsearchSettings.SectionName))
            .Validate(options => options.IsValid(out _), "Elasticsearch configuration is invalid.")
            .ValidateOnStart();

        builder.Services.AddOptions<InputFieldPathSettings>()
            .Bind(builder.Configuration.GetSection(InputFieldPathSettings.SectionName))
            .Validate(options => options.IsValid(out _), "InputFieldPaths configuration is invalid.")
            .ValidateOnStart();

        builder.Services.AddOptions<OutputSettings>()
            .Bind(builder.Configuration.GetSection(OutputSettings.SectionName))
            .Validate(options => options.IsValid(out _), "Output configuration is invalid.")
            .ValidateOnStart();

        builder.Services.AddSingleton(provider =>
        {
            var options = provider.GetRequiredService<IOptions<ElasticsearchSettings>>().Value;
            var settings = new ElasticsearchClientSettings(new Uri(options.Uri))
                .DefaultIndex(options.IndexName)
                .RequestTimeout(TimeSpan.FromSeconds(options.RequestTimeoutSeconds));

            if (!string.IsNullOrWhiteSpace(options.ApiKey))
            {
                settings = settings.Authentication(new ApiKey(options.ApiKey));
            }
            else if (!string.IsNullOrWhiteSpace(options.Username) && !string.IsNullOrWhiteSpace(options.Password))
            {
                settings = settings.Authentication(new BasicAuthentication(options.Username, options.Password));
            }

            return new ElasticsearchClient(settings);
        });

        builder.Services.AddSingleton<GatewayHealthState>();
        builder.Services.AddSingleton<JsonPathReader>();
        builder.Services.AddSingleton<GeometryExtractor>();
        builder.Services.AddSingleton<InputMessageValidator>();
        builder.Services.AddSingleton<JsonOutputBuilder>();
        builder.Services.AddSingleton<RuleValidator>();
        builder.Services.AddSingleton<RuleMatcher>();
        builder.Services.AddSingleton<IRuleRepository, ElasticsearchRuleRepository>();
        builder.Services.AddSingleton<RuleCache>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<RuleCache>());
        builder.Services.AddSingleton<RabbitMqConnectionFactory>();
        builder.Services.AddSingleton<RabbitMqGatewayPublisher>();
        builder.Services.AddSingleton<RabbitMqGatewayConsumer>();

        builder.Services.AddHostedService<Worker>();

        await builder.Build().RunAsync();
    }
}
