using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
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

        builder.Services.AddSingleton<IMessageTraceContextPropagator, W3CMessageTraceContextPropagator>();

        // Propagation remains W3C even when signal collection is disabled.
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        var settings = RuntimeObservabilitySettings.Read(builder.Configuration, defaultServiceName);
        if (!settings.Enabled)
        {
            return builder;
        }

        if (settings.TracesEnabled && settings.TracesOtlpEnabled)
        {
            ValidateTraceExporterConfiguration(builder.Configuration);
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
                        options.Filter = context =>
                        {
                            if (settings.PrometheusEnabled &&
                                context.Request.Path.StartsWithSegments(
                                    settings.PrometheusPath,
                                    StringComparison.OrdinalIgnoreCase))
                            {
                                return false;
                            }

                            return !settings.ExcludeHealthChecks ||
                                (!context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
                                 && !context.Request.Path.StartsWithSegments("/live", StringComparison.OrdinalIgnoreCase)
                                 && !context.Request.Path.StartsWithSegments("/ready", StringComparison.OrdinalIgnoreCase));
                        };
                    });
                }

                if (settings.TracesOtlpEnabled)
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
                    metrics.AddMeter("Microsoft.AspNetCore.Hosting", "Microsoft.AspNetCore.Server.Kestrel");
                }

                AddHistogramViews(metrics);
                if (settings.PrometheusEnabled)
                {
                    metrics.AddPrometheusExporter(options =>
                    {
                        options.ScrapeEndpointPath = settings.PrometheusPath;
                    });
                }
            });
        }

        EcsLoggingRegistration.Configure(
            builder,
            settings.ResourceIdentity,
            settings.LogsEnabled);

        return builder;
    }

    public static WebApplicationBuilder ConfigureImagingPipelinePrometheusListener(
        this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var settings = PrometheusRuntimeSettings.Read(builder.Configuration);
        if (RuntimeObservabilitySettings.IsSdkEnabled(builder.Configuration) &&
            RuntimeObservabilitySettings.IsMetricsEnabled(builder.Configuration) &&
            settings.Enabled)
        {
            builder.WebHost.ConfigureKestrel(options => options.ListenAnyIP(settings.Port));
        }
        return builder;
    }

    public static WebApplication MapImagingPipelinePrometheusScrapingEndpoint(
        this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        var settings = PrometheusRuntimeSettings.Read(app.Configuration);
        if (!RuntimeObservabilitySettings.IsSdkEnabled(app.Configuration) ||
            !RuntimeObservabilitySettings.IsMetricsEnabled(app.Configuration) ||
            !settings.Enabled)
        {
            return app;
        }

        app.MapPrometheusScrapingEndpoint(settings.Path)
            .DisableHttpMetrics();
        return app;
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
                     TelemetryMetricNames.RabbitMqChannelWaitDuration,
                     TelemetryMetricNames.DependencyDuration,
                     TelemetryMetricNames.PipelineStageDuration,
                     TelemetryMetricNames.LogExportDuration,
                     TelemetryMetricNames.GatewayRuleCacheRefreshDuration,
                     TelemetryMetricNames.RulesOperationDuration
                 })
        {
            metrics.AddView(metricName, latencyConfiguration);
        }

        foreach (var metricName in new[]
                 {
                     TelemetryMetricNames.MessagingDeliveryDelay,
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

    private static bool ReadStrictBoolean(
        IConfiguration configuration,
        string key,
        bool defaultValue)
    {
        var raw = configuration[key];
        if (raw is null)
        {
            return defaultValue;
        }

        return bool.TryParse(raw, out var value)
            ? value
            : throw new InvalidOperationException($"Configuration value '{key}' must be 'true' or 'false'.");
    }

    private static void ValidateTraceExporterConfiguration(IConfiguration configuration)
    {
        const string signalEndpointKey = "OTEL_EXPORTER_OTLP_TRACES_ENDPOINT";
        const string baseEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";
        const string signalProtocolKey = "OTEL_EXPORTER_OTLP_TRACES_PROTOCOL";
        const string baseProtocolKey = "OTEL_EXPORTER_OTLP_PROTOCOL";

        var endpointKey = !string.IsNullOrWhiteSpace(configuration[signalEndpointKey])
            ? signalEndpointKey
            : baseEndpointKey;
        var endpointValue = configuration[endpointKey];
        if (string.IsNullOrWhiteSpace(endpointValue))
        {
            throw new InvalidOperationException(
                $"Trace OTLP export is enabled, but neither '{signalEndpointKey}' nor '{baseEndpointKey}' is configured.");
        }

        if (!Uri.TryCreate(endpointValue, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(endpoint.Host))
        {
            throw new InvalidOperationException(
                $"Configuration value '{endpointKey}' must be an absolute HTTP or HTTPS URL.");
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new InvalidOperationException(
                $"Configuration value '{endpointKey}' must not contain credentials.");
        }

        var protocolKey = !string.IsNullOrWhiteSpace(configuration[signalProtocolKey])
            ? signalProtocolKey
            : baseProtocolKey;
        var protocol = configuration[protocolKey];
        if (string.IsNullOrWhiteSpace(protocol))
        {
            throw new InvalidOperationException(
                $"Trace OTLP export is enabled, but neither '{signalProtocolKey}' nor '{baseProtocolKey}' is configured. Use 'grpc' or 'http/protobuf'.");
        }

        if (!protocol.Equals("grpc", StringComparison.OrdinalIgnoreCase)
            && !protocol.Equals("http/protobuf", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Configuration value '{protocolKey}' must be 'grpc' or 'http/protobuf'.");
        }
    }

    private sealed record RuntimeObservabilitySettings(
        bool Enabled,
        string ServiceName,
        string ServiceNamespace,
        string ServiceVersion,
        string DeploymentEnvironment,
        string ServiceInstanceId,
        IReadOnlyList<KeyValuePair<string, object>> ResourceAttributes,
        ObservabilityResourceIdentity ResourceIdentity,
        bool TracesEnabled,
        bool RecordExceptions,
        bool ExcludeHealthChecks,
        bool TracesOtlpEnabled,
        bool SamplerConfigured,
        double DefaultSamplingRatio,
        bool MetricsEnabled,
        bool PrometheusEnabled,
        string PrometheusPath,
        int PrometheusPort,
        bool LogsEnabled)
    {
        public static bool IsSdkEnabled(IConfiguration configuration)
        {
            var prefix = ImagingPipelineObservabilityOptions.SectionName;
            return ReadBoolean(configuration, $"{prefix}:Enabled", true)
                && !ReadBoolean(configuration, "OTEL_SDK_DISABLED", false);
        }

        public static bool IsMetricsEnabled(IConfiguration configuration)
        {
            var prefix = ImagingPipelineObservabilityOptions.SectionName;
            return ReadBoolean(configuration, $"{prefix}:Metrics:Enabled", true);
        }

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
                configuration["SERVICE_VERSION"],
                Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                "unknown")!;
            var deploymentEnvironment = FirstNonEmpty(
                configuration[$"{prefix}:DeploymentEnvironment"],
                configuration["DEPLOYMENT_ENVIRONMENT"],
                configuration["DOTNET_ENVIRONMENT"],
                configuration["ASPNETCORE_ENVIRONMENT"],
                "unknown")!;

            var podUid = configuration["POD_UID"];
            var podName = configuration["POD_NAME"];
            var podNamespace = configuration["POD_NAMESPACE"];
            var deploymentName = configuration["DEPLOYMENT_NAME"];
            var nodeName = configuration["NODE_NAME"];
            var containerName = configuration["CONTAINER_NAME"];
            var serviceInstanceId = FirstNonEmpty(
                podUid,
                podName,
                $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}")!;

            var resourceIdentity = new ObservabilityResourceIdentity(
                serviceName,
                serviceNamespace,
                serviceVersion,
                deploymentEnvironment,
                serviceInstanceId,
                podName,
                podUid,
                podNamespace,
                deploymentName,
                nodeName,
                containerName);
            var attributes = BuildResourceAttributes(
                resourceIdentity,
                configuration["CLUSTER_NAME"]);
            var prometheus = PrometheusRuntimeSettings.Read(configuration);
            return new RuntimeObservabilitySettings(
                IsSdkEnabled(configuration),
                serviceName,
                serviceNamespace,
                serviceVersion,
                deploymentEnvironment,
                serviceInstanceId,
                attributes,
                resourceIdentity,
                ReadBoolean(configuration, $"{prefix}:Traces:Enabled", true),
                ReadBoolean(configuration, $"{prefix}:Traces:RecordExceptions", true),
                ReadBoolean(configuration, $"{prefix}:Traces:ExcludeHealthChecks", true),
                ReadStrictBoolean(configuration, $"{prefix}:Traces:OtlpEnabled", true),
                !string.IsNullOrWhiteSpace(configuration["OTEL_TRACES_SAMPLER"]),
                ReadSamplingRatio(configuration, $"{prefix}:Traces:DefaultSamplingRatio"),
                IsMetricsEnabled(configuration),
                prometheus.Enabled,
                prometheus.Path,
                prometheus.Port,
                ReadBoolean(configuration, $"{prefix}:Logs:Enabled", true));
        }

        private static IReadOnlyList<KeyValuePair<string, object>> BuildResourceAttributes(
            ObservabilityResourceIdentity resource,
            string? clusterName)
        {
            var attributes = new List<KeyValuePair<string, object>>
            {
                new("deployment.environment", resource.DeploymentEnvironment),
                new("deployment.environment.name", resource.DeploymentEnvironment),
                new("host.name", resource.HostName),
                new("process.pid", resource.ProcessId),
                new("process.runtime.name", resource.RuntimeName),
                new("process.runtime.version", resource.RuntimeVersion),
                new("process.runtime.description", resource.RuntimeDescription)
            };
            AddIfPresent(attributes, "service.node.name", resource.PodName);
            AddIfPresent(attributes, "k8s.cluster.name", clusterName);
            AddIfPresent(attributes, "k8s.namespace.name", resource.PodNamespace);
            AddIfPresent(attributes, "k8s.deployment.name", resource.DeploymentName);
            AddIfPresent(attributes, "k8s.pod.name", resource.PodName);
            AddIfPresent(attributes, "k8s.pod.uid", resource.PodUid);
            AddIfPresent(attributes, "k8s.node.name", resource.NodeName);
            AddIfPresent(attributes, "k8s.container.name", resource.ContainerName);
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
            ReadStrictBoolean(configuration, key, defaultValue);

        private static double ReadSamplingRatio(IConfiguration configuration, string key)
        {
            var raw = configuration[key];
            if (string.IsNullOrWhiteSpace(raw))
            {
                return 1.0;
            }

            if (double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
                && ratio is >= 0 and <= 1)
            {
                return ratio;
            }

            throw new InvalidOperationException($"Configuration value '{key}' must be a number from 0 through 1.");
        }

        private static string? FirstNonEmpty(params string?[] values) =>
            values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));
    }

    private sealed record PrometheusRuntimeSettings(
        bool Enabled,
        string Path,
        int Port)
    {
        private const string DefaultPath = "/metrics";
        private const int DefaultPort = 9464;

        public static PrometheusRuntimeSettings Read(IConfiguration configuration)
        {
            var prefix = $"{ImagingPipelineObservabilityOptions.SectionName}:Metrics:Prometheus";
            var enabled = ReadStrictBoolean(configuration, $"{prefix}:Enabled", true);
            var path = ReadPath(configuration, $"{prefix}:Path");
            var port = ReadPort(configuration, $"{prefix}:Port");
            return new PrometheusRuntimeSettings(enabled, path, port);
        }

        private static string ReadPath(IConfiguration configuration, string key)
        {
            var raw = configuration[key];
            if (raw is null)
            {
                return DefaultPath;
            }

            if (string.IsNullOrWhiteSpace(raw) ||
                raw[0] != '/' ||
                raw.Length == 1 ||
                raw.IndexOfAny(['?', '#', '{', '}', '*']) >= 0 ||
                raw.Any(char.IsWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Configuration value '{key}' must be an absolute HTTP path such as '/metrics'.");
            }

            return raw;
        }

        private static int ReadPort(IConfiguration configuration, string key)
        {
            var raw = configuration[key];
            if (raw is null)
            {
                return DefaultPort;
            }

            if (int.TryParse(
                    raw,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port) &&
                port is >= 1 and <= 65_535)
            {
                return port;
            }

            throw new InvalidOperationException(
                $"Configuration value '{key}' must be an integer from 1 through 65535.");
        }
    }
}
