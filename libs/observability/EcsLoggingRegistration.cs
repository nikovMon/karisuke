using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImagingPipeline.Observability;

internal static class EcsLoggingRegistration
{
    public static void Configure(
        IHostApplicationBuilder builder,
        ObservabilityResourceIdentity resource,
        bool logsEnabled)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(resource);

        if (!logsEnabled)
        {
            return;
        }

        var configuration = builder.Configuration;
        var logstashEnabled = ReadBoolean(
            configuration["Observability:Logs:Logstash:Enabled"],
            "Observability:Logs:Logstash:Enabled",
            defaultValue: true);
        var consoleEnabled = ReadBoolean(
            configuration["Observability:Logs:ConsoleEnabled"],
            "Observability:Logs:ConsoleEnabled",
            defaultValue: false);
        if (!logstashEnabled && !consoleEnabled)
        {
            throw new InvalidOperationException(
                "Observability logs are enabled, but neither Logstash HTTP logging nor console logging is enabled.");
        }

        builder.Logging.ClearProviders();
        if (consoleEnabled)
        {
            builder.Logging.Configure(options =>
            {
                options.ActivityTrackingOptions =
                    ActivityTrackingOptions.TraceId |
                    ActivityTrackingOptions.SpanId;
            });
            builder.Logging.AddJsonConsole(options =>
            {
                options.IncludeScopes = true;
                options.UseUtcTimestamp = true;
                options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
            });
        }

        if (!logstashEnabled)
        {
            return;
        }

        var options = LogstashHttpOptions.Read(configuration);
        var dataStream = LogstashHttpOptions.ReadDataStream(
            configuration,
            resource.DeploymentEnvironment);
        var buffer = new EcsLogBuffer(options);

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton(dataStream);
        builder.Services.AddSingleton(resource);
        builder.Services.AddSingleton(buffer);
        builder.Services.AddSingleton<EcsHttpLoggerProvider>();
        builder.Services.AddSingleton<ILoggerProvider>(
            static services => services.GetRequiredService<EcsHttpLoggerProvider>());
        builder.Services.AddSingleton<EcsLogstashExporterService>();
        builder.Services.AddSingleton<IHostedService>(
            static services => services.GetRequiredService<EcsLogstashExporterService>());
    }

    private static bool ReadBoolean(string? raw, string key, bool defaultValue)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return defaultValue;
        }

        if (bool.TryParse(raw, out var value))
        {
            return value;
        }

        throw new InvalidOperationException(
            $"Configuration value '{key}' must be 'true' or 'false'.");
    }
}
