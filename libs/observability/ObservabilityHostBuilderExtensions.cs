using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Logs;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ImagingPipeline.Observability;

public static class ObservabilityHostBuilderExtensions
{
    private static readonly double[] LatencySecondsBuckets =
    [
        0.005, 0.01, 0.025, 0.05, 0.075, 0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60
    ];
    private static readonly double[] PipelineTransitSecondsBuckets =
    [
        0.1, 0.25, 0.5, 0.75, 1, 2.5, 5, 7.5, 10, 30, 60, 120, 300, 600, 1_800, 3_600, 21_600, 86_400
    ];

    private static readonly double[] SizeBytesBuckets =
    [
        256, 1_024, 4_096, 16_384, 65_536, 262_144, 1_048_576, 4_194_304, 16_777_216
    ];

    private static readonly double[] CountBuckets =
    [
        1, 2, 4, 8, 16, 32, 64, 128, 256, 512, 1_024, 4_096
    ];

    public static IHostApplicationBuilder AddImagingPipelineObservability(
        this IHostApplicationBuilder builder,
        string defaultServiceName,
        bool instrumentAspNetCore = false)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultServiceName);

        var section = builder.Configuration.GetSection(ImagingPipelineObservabilityOptions.SectionName);
        builder.Services.AddOptions<ImagingPipelineObservabilityOptions>().Bind(section);
        builder.Services.AddSingleton<IMessageTraceContextPropagator, W3CMessageTraceContextPropagator>();

        // Propagation remains W3C even when signal collection is disabled.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var settings = RuntimeObservabilitySettings.Read(builder.Configuration, defaultServiceName);
        if (!settings.Enabled)
        {
            return builder;
        }

        Action<ResourceBuilder> configureResource = resource => resource
            .AddService(
                serviceName: settings.ServiceName,
                serviceNamespace: settings.ServiceNamespace,
                serviceVersion: settings.ServiceVersion,
                serviceInstanceId: settings.ServiceInstanceId)
            .AddAttributes(settings.ResourceAttributes);

        var openTelemetry = builder.Services
            .AddOpenTelemetry()
            .ConfigureResource(configureResource);

        if (settings.TracesEnabled)
        {
            openTelemetry.WithTracing(tracing =>
            {
                if (!settings.SamplerConfigured)
                {
                    tracing.SetSampler(new ParentBasedSampler(
                        new TraceIdRatioBasedSampler(settings.DefaultSamplingRatio)));
                }

                tracing
                    .AddSource(TelemetrySourceNames.All)
                    .AddSource(
                        TelemetrySourceNames.RabbitMqClientPublisher,
                        TelemetrySourceNames.RabbitMqClientSubscriber)
                    .AddHttpClientInstrumentation(options =>
                    {
                        options.RecordException = settings.RecordExceptions;
                    });

                if (instrumentAspNetCore)
                {
                    tracing.AddAspNetCoreInstrumentation(options =>
                    {
                        options.RecordException = settings.RecordExceptions;
                        if (settings.ExcludeHealthChecks)
                        {
                            options.Filter = context =>
                                !context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                                && !context.Request.Path.StartsWithSegments("/live", StringComparison.OrdinalIgnoreCase)
                                && !context.Request.Path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase);
                        }
                    });
                }

                if (settings.OtlpEnabled)
                {
                    tracing.AddOtlpExporter();
                }
            });
        }

        if (settings.MetricsEnabled)
        {
            openTelemetry.WithMetrics(metrics =>
            {
                metrics
                    .AddMeter(TelemetrySourceNames.All)
                    .AddRuntimeInstrumentation()
                    .AddHttpClientInstrumentation();

                if (instrumentAspNetCore)
                {
                    metrics.AddAspNetCoreInstrumentation();
                }

                if (instrumentAspNetCore)
                {
                    metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");
                }

                AddHistogramViews(metrics);
                if (settings.OtlpEnabled)
                {
                    metrics.AddOtlpExporter();
                }
            });
        }

        if (settings.LogsEnabled)
        {
            builder.Logging.ClearProviders();
            if (settings.ConsoleLogsEnabled)
            {
                // The JSON-console path needs explicit identifiers. OTLP LogRecord already
                // carries native trace/span IDs, so adding these scopes there duplicates data.
                builder.Logging.Configure(options =>
                {
                    options.ActivityTrackingOptions =
                        ActivityTrackingOptions.TraceId |
                        ActivityTrackingOptions.SpanId;
                });
            }

            if (settings.ConsoleLogsEnabled)
            {
                builder.Logging.AddJsonConsole(options =>
                {
                    options.IncludeScopes = settings.IncludeLogScopes;
                    options.UseUtcTimestamp = true;
                    options.TimestampFormat = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";
                });
            }

            var logResource = ResourceBuilder.CreateDefault();
            configureResource(logResource);

            builder.Logging.AddOpenTelemetry(options =>
            {
                options.SetResourceBuilder(logResource);
                options.IncludeFormattedMessage = settings.IncludeFormattedLogMessage;
                options.IncludeScopes = settings.IncludeLogScopes;
                options.ParseStateValues = settings.ParseLogStateValues;
                if (settings.OtlpLogsEnabled)
                {
                    options.AddOtlpExporter();
                }
            });
        }

        return builder;
    }

    private static void AddHistogramViews(MeterProviderBuilder metrics)
    {
        var latencyConfiguration = new ExplicitBucketHistogramConfiguration
        {
            Boundaries = LatencySecondsBuckets
        };
        var sizeConfiguration = new ExplicitBucketHistogramConfiguration
        {
            Boundaries = SizeBytesBuckets
        };
        var countConfiguration = new ExplicitBucketHistogramConfiguration
        {
            Boundaries = CountBuckets
        };
        var pipelineTransitConfiguration = new ExplicitBucketHistogramConfiguration
        {
            Boundaries = PipelineTransitSecondsBuckets
        };

        foreach (var metricName in new[]
                 {
                     TelemetryMetricNames.MessagingClientDuration,
                     TelemetryMetricNames.MessagingProcessDuration,
                     TelemetryMetricNames.MessagingDeliveryDelay,
                     TelemetryMetricNames.RabbitMqChannelWaitDuration,
                     TelemetryMetricNames.DependencyDuration,
                     TelemetryMetricNames.PipelineStageDuration,
                     TelemetryMetricNames.GatewayRuleCacheRefreshDuration,
                     TelemetryMetricNames.RulesOperationDuration
                 })
        {
            metrics.AddView(metricName, latencyConfiguration);
        }

        foreach (var metricName in new[]
                 {
                     TelemetryMetricNames.PipelineExternalStageDuration,
                     TelemetryMetricNames.PipelineEndToEndDuration
                 })
        {
            metrics.AddView(metricName, pipelineTransitConfiguration);
        }

        foreach (var metricName in new[]
                 {
                     TelemetryMetricNames.MessagingBodySize,
                     TelemetryMetricNames.DependencyPayloadSize,
                     TelemetryMetricNames.PipelinePayloadSize
                 })
        {
            metrics.AddView(metricName, sizeConfiguration);
        }

        foreach (var metricName in new[]
                 {
                     TelemetryMetricNames.DependencyBatchSize,
                     TelemetryMetricNames.PipelineFanOut,
                     TelemetryMetricNames.PipelineBatchSize,
                     TelemetryMetricNames.GatewayRulesEvaluated,
                     TelemetryMetricNames.GatewayRulesMatched,
                     TelemetryMetricNames.RulesDocuments,
                     TelemetryMetricNames.RulesBatchSize
                 })
        {
            metrics.AddView(metricName, countConfiguration);
        }
    }

    private sealed record RuntimeObservabilitySettings(
        bool Enabled,
        string ServiceName,
        string ServiceNamespace,
        string ServiceVersion,
        string ServiceInstanceId,
        IReadOnlyList<KeyValuePair<string, object>> ResourceAttributes,
        bool TracesEnabled,
        bool RecordExceptions,
        bool ExcludeHealthChecks,
        bool SamplerConfigured,
        double DefaultSamplingRatio,
        bool MetricsEnabled,
        bool LogsEnabled,
        bool ConsoleLogsEnabled,
        bool IncludeFormattedLogMessage,
        bool IncludeLogScopes,
        bool ParseLogStateValues,
        bool OtlpEnabled,
        bool OtlpLogsEnabled)
    {
        public static RuntimeObservabilitySettings Read(IConfiguration configuration, string defaultServiceName)
        {
            var prefix = ImagingPipelineObservabilityOptions.SectionName;
            var serviceName = FirstNonEmpty(
                configuration["OTEL_SERVICE_NAME"],
                configuration[$"{prefix}:ServiceName"],
                defaultServiceName)!;
            var serviceNamespace = FirstNonEmpty(
                configuration[$"{prefix}:ServiceNamespace"],
                ObservabilityServiceNames.Namespace)!;
            var serviceVersion = FirstNonEmpty(
                configuration[$"{prefix}:ServiceVersion"],
                Environment.GetEnvironmentVariable("SERVICE_VERSION"),
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                "unknown")!;
            var deploymentEnvironment = FirstNonEmpty(
                configuration[$"{prefix}:DeploymentEnvironment"],
                Environment.GetEnvironmentVariable("DEPLOYMENT_ENVIRONMENT"),
                configuration["DOTNET_ENVIRONMENT"],
                configuration["ASPNETCORE_ENVIRONMENT"]);

            var podUid = Environment.GetEnvironmentVariable("POD_UID");
            var podName = Environment.GetEnvironmentVariable("POD_NAME");
            var serviceInstanceId = FirstNonEmpty(
                podUid,
                podName,
                $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}")!;

            var attributes = BuildOpenShiftResourceAttributes(deploymentEnvironment, podName, podUid);
            var otlpEnabled = ReadBoolean(configuration, $"{prefix}:Otlp:Enabled", true);
            return new RuntimeObservabilitySettings(
                ReadBoolean(configuration, $"{prefix}:Enabled", true)
                    && !ReadBoolean(configuration, "OTEL_SDK_DISABLED", false),
                serviceName,
                serviceNamespace,
                serviceVersion,
                serviceInstanceId,
                attributes,
                ReadBoolean(configuration, $"{prefix}:Traces:Enabled", true),
                ReadBoolean(configuration, $"{prefix}:Traces:RecordExceptions", true),
                ReadBoolean(configuration, $"{prefix}:Traces:ExcludeHealthChecks", true),
                !string.IsNullOrWhiteSpace(configuration["OTEL_TRACES_SAMPLER"]),
                ReadSamplingRatio(configuration, $"{prefix}:Traces:DefaultSamplingRatio"),
                ReadBoolean(configuration, $"{prefix}:Metrics:Enabled", true),
                ReadBoolean(configuration, $"{prefix}:Logs:Enabled", true),
                ReadBoolean(configuration, $"{prefix}:Logs:ConsoleEnabled", false),
                ReadBoolean(configuration, $"{prefix}:Logs:IncludeFormattedMessage", true),
                ReadBoolean(configuration, $"{prefix}:Logs:IncludeScopes", true),
                ReadBoolean(configuration, $"{prefix}:Logs:ParseStateValues", true),
                otlpEnabled,
                otlpEnabled && ReadBoolean(configuration, $"{prefix}:Logs:OtlpEnabled", true));
        }

        private static IReadOnlyList<KeyValuePair<string, object>> BuildOpenShiftResourceAttributes(
            string? deploymentEnvironment,
            string? podName,
            string? podUid)
        {
            var attributes = new List<KeyValuePair<string, object>>();
            AddIfPresent(attributes, "deployment.environment.name", deploymentEnvironment);

            AddIfPresent(attributes, "k8s.namespace.name", Environment.GetEnvironmentVariable("POD_NAMESPACE"));
            AddIfPresent(attributes, "k8s.deployment.name", Environment.GetEnvironmentVariable("DEPLOYMENT_NAME"));
            AddIfPresent(attributes, "k8s.pod.name", podName);
            AddIfPresent(attributes, "k8s.pod.uid", podUid);
            AddIfPresent(attributes, "k8s.node.name", Environment.GetEnvironmentVariable("NODE_NAME"));
            AddIfPresent(attributes, "k8s.container.name", Environment.GetEnvironmentVariable("CONTAINER_NAME"));
            return attributes;
        }

        private static void AddIfPresent(
            ICollection<KeyValuePair<string, object>> attributes,
            string key,
            string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                attributes.Add(new KeyValuePair<string, object>(key, value));
            }
        }

        private static bool ReadBoolean(IConfiguration configuration, string key, bool defaultValue) =>
            bool.TryParse(configuration[key], out var value) ? value : defaultValue;

        private static double ReadSamplingRatio(IConfiguration configuration, string key)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 0.10;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
                && ratio is >= 0 and <= 1)
            {
                return ratio;
            }

            throw new InvalidOperationException($"Configuration value '{key}' must be a number from 0 through 1.");
        }

        private static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
