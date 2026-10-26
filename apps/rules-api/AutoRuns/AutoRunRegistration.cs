using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.PipelineCatalog;
using ImagingPipeline.RabbitMqConfiguration;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public static class AutoRunRegistration
{
    public static IServiceCollection AddAutoRuns(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<AutoRunOptions>()
            .Bind(configuration.GetSection(AutoRunOptions.SectionName))
            .PostConfigure(options =>
            {
                if (options.Destination is not { } destination) return;
                options.Destination = destination with
                {
                    QueueArguments = Normalize(destination.QueueArguments, "AutoRun:Destination:QueueArguments"),
                    ExchangeArguments = Normalize(destination.ExchangeArguments, "AutoRun:Destination:ExchangeArguments"),
                    BindingArguments = Normalize(destination.BindingArguments, "AutoRun:Destination:BindingArguments")
                };
            })
            .Validate(options => options.IsValid(), "AutoRun limits, publication settings, or gateway destination are invalid.")
            .ValidateOnStart();
        services.AddOptions<ElasticProviderOptions>()
            .Bind(configuration.GetSection(ElasticProviderOptions.SectionName))
            .PostConfigure(options =>
            {
                // A supplied catalog replaces the defaults, so deployments can close the list to a subset.
                var section = configuration.GetSection("AutoRun:Provider:SensorTypeMappings");
                var sectionDeclared = configuration is IConfigurationRoot root &&
                    root.Providers.Any(provider => provider.TryGet(section.Path, out _));
                if (section.Exists() || sectionDeclared)
                    options.SensorTypeMappings = section.Get<Dictionary<string, string>>() ?? [];
            })
            .Validate(options => ElasticProviderHeaders.IsValid(options.Headers) &&
                ElasticProviderHeaders.HasValidConfigurationShape(configuration.GetSection("AutoRun:Provider:Headers")),
                ElasticProviderHeaders.ValidationError)
            .Validate(options => options.TimeoutSeconds > 0,
                "Elastic Provider timeout must be positive.")
            .Validate(options => options.RequestDelayMs is >= 0 and <= 60_000,
                "Elastic Provider RequestDelayMs must be between 0 and 60000.")
            .Validate(options => options.MaxRetries is >= 0 and <= 5,
                "Elastic Provider MaxRetries must be between 0 and 5.")
            .Validate(options => options.RetryDelayMs is >= 1 and <= 60_000,
                "Elastic Provider RetryDelayMs must be between 1 and 60000.")
            .Validate(options => SensorTypeCatalog.IsValid(options.SensorTypeMappings),
                SensorTypeCatalog.ValidationError)
            .Validate(options => options.To is >= 0 and <= 10_000,
                "Elastic Provider To must be between 0 and 10000.")
            .Validate(options => options.MaxResponseBytes is > 0 and <= 536_870_912,
                "Elastic Provider MaxResponseBytes must be between 1 and 536870912 bytes.")
            .Validate(options => !configuration.GetValue<bool>("AutoRun:Enabled") ||
                Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var uri) &&
                uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo),
                "Enabled auto-run requires an HTTP(S) Elastic Provider BaseUrl without credentials.")
            .ValidateOnStart();
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAutoRunMemoryGuard>(provider =>
            new AutoRunMemoryGuard(provider.GetRequiredService<IOptions<AutoRunOptions>>()));
        services.AddRabbitMqDestinationPublisher(configuration);
        services.AddHttpClient<IElasticOverlayProvider, ElasticOverlayProvider>(client =>
            client.Timeout = Timeout.InfiniteTimeSpan)
            .RemoveAllLoggers()
            .RedactLoggedHeaders(static _ => true)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<ProviderQueryBuilder>();
        services.AddSingleton<IAutoRunRuleResolver, AutoRunRuleResolver>();
        services.AddSingleton<IAutoRunPublisher, AutoRunPublisher>();
        services.AddSingleton<AutoRunService>();
        services.AddHostedService(provider => provider.GetRequiredService<AutoRunService>());
        return services;
    }

    private static IReadOnlyDictionary<string, object?> Normalize(IReadOnlyDictionary<string, object?> arguments, string path)
    {
        // Validate scalar configuration, but preserve the gateway input client's conversion
        // policy (rather than the catalog's output-destination numeric conversions).
        _ = RabbitMqOptionsValidation.NormalizeArguments(arguments, path);
        return RabbitMqArgumentNormalizer.Normalize(arguments, RabbitMqArgumentConversionPolicy.Legacy);
    }
}
