namespace ImagingPipeline.Observability;

public sealed class ImagingPipelineObservabilityOptions
{
    public const string SectionName = "Observability";

    public bool Enabled { get; set; } = true;
    public string? ServiceName { get; set; }
    public string ServiceNamespace { get; set; } = ObservabilityServiceNames.Namespace;
    public string? ServiceVersion { get; set; }
    public string? DeploymentEnvironment { get; set; }
    public ObservabilityTraceOptions Traces { get; set; } = new();
    public ObservabilityMetricOptions Metrics { get; set; } = new();
    public ObservabilityLogOptions Logs { get; set; } = new();
}

public sealed class ObservabilityTraceOptions
{
    public bool Enabled { get; set; } = true;
    public bool RecordExceptions { get; set; } = true;
    public bool ExcludeHealthChecks { get; set; } = true;
    public bool OtlpEnabled { get; set; } = true;
    public double DefaultSamplingRatio { get; set; } = 1.0;
}

public sealed class ObservabilityMetricOptions
{
    public bool Enabled { get; set; } = true;
    public ObservabilityPrometheusOptions Prometheus { get; set; } = new();
}

public sealed class ObservabilityPrometheusOptions
{
    public bool Enabled { get; set; } = true;
    public string Path { get; set; } = "/metrics";
    public int Port { get; set; } = 9464;
}

public sealed class ObservabilityLogOptions
{
    public bool Enabled { get; set; } = true;
    public bool ConsoleEnabled { get; set; }
    public ObservabilityLogstashOptions Logstash { get; set; } = new();
    public ObservabilityLogDataStreamOptions DataStream { get; set; } = new();
}

public sealed class ObservabilityLogstashOptions
{
    public bool Enabled { get; set; } = true;
    public string? Endpoint { get; set; }
    public int QueueCapacity { get; set; } = 10_000;
    public int PriorityQueueCapacity { get; set; } = 1_000;
    public int BatchSize { get; set; } = 100;
    public int FlushIntervalMilliseconds { get; set; } = 1_000;
    public int RequestTimeoutSeconds { get; set; } = 5;
    public int MaxRetryAttempts { get; set; } = 3;
    public int RetryBaseDelayMilliseconds { get; set; } = 200;
    public int ShutdownFlushTimeoutSeconds { get; set; } = 5;
    public int MaxAttributeCount { get; set; } = 64;
    public int MaxCollectionCount { get; set; } = 32;
    public int MaxStringLength { get; set; } = 8_192;
}

public sealed class ObservabilityLogDataStreamOptions
{
    public string Dataset { get; set; } = "findair";
    public string? Namespace { get; set; }
}
