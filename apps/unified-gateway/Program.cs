using ImagingPipeline.ElasticsearchClient;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.RuleEngine.Loading;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.UnifiedGateway.Configuration;
using ImagingPipeline.UnifiedGateway.Dispatch;
using ImagingPipeline.UnifiedGateway.Processing;
using ImagingPipeline.UnifiedGateway.Rules;
using ImagingPipeline.UnifiedGateway.Source;

namespace ImagingPipeline.UnifiedGateway;

public sealed partial class Program
{
    public static async Task Main(string[] args)
    {
        var builder = GatewayApplicationBuilder.Create(args);
        builder.AddImagingPipelineObservability(
            ObservabilityServiceNames.UnifiedGateway,
            instrumentAspNetCore: true);
        builder.ConfigureImagingPipelinePrometheusListener();

        builder.Services.AddPipelineContracts();
        builder.Services.AddPipelineCatalog(builder.Configuration);
        builder.Services.AddSingleton<PipelineWorkPreparer>();
        builder.Services.AddRabbitMqDestinationPublisher(builder.Configuration);
        builder.Services.AddSingleton<IDispatchTransport, RabbitMqDispatchTransport>();
        // Redirects are not followed and the catalog timeout is applied per request by the transport.
        builder.Services.AddHttpClient(HttpDispatchTransport.HttpClientName)
            .ConfigureHttpClient(client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        builder.Services.AddSingleton<IDispatchTransport, HttpDispatchTransport>();
        builder.Services.AddSingleton<PipelineDispatcher>();

        builder.Services.AddElasticsearchClient(builder.Configuration);
        builder.Services.AddSingleton<IRuleRepository, ElasticsearchRuleRepository>();
        builder.Services.AddOptions<RuleRefreshOptions>()
            .Bind(builder.Configuration.GetSection(RuleRefreshOptions.SectionName))
            .Validate(options => options.IsValid(), "RuleRefresh needs IntervalSeconds > 0 and JitterSeconds >= 0.")
            .ValidateOnStart();
        builder.Services.AddSingleton<GatewayRuleCache>();
        builder.Services.AddHostedService(provider => provider.GetRequiredService<GatewayRuleCache>());

        // Hosted services start in registration order, so rules are loaded before consuming starts.
        builder.Services.AddRabbitMqConsumer(builder.Configuration);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<RuleMatcher>();
        builder.Services.AddSingleton<IRabbitMqMessageHandler, SourceMessageHandler>();
        builder.Services.AddHostedService<RabbitMqConsumerService>();

        var app = builder.Build();

        app.MapGet("/health", () => Results.Ok(new
        {
            status = "Healthy",
            capabilities = new[] { "catalog", "contracts", "rules", "dispatch" },
            dispatchActive = true
        }));
        app.MapImagingPipelinePrometheusScrapingEndpoint();

        await app.RunAsync();
    }
}
