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
    public ObservabilityOtlpOptions Otlp { get; set; } = new();
}

public sealed class ObservabilityOtlpOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class ObservabilityTraceOptions
{
    public bool Enabled { get; set; } = true;
    public bool RecordExceptions { get; set; } = true;
    public bool ExcludeHealthChecks { get; set; } = true;
    public double DefaultSamplingRatio { get; set; } = 0.10;
}

public sealed class ObservabilityMetricOptions
{
    public bool Enabled { get; set; } = true;
}

public sealed class ObservabilityLogOptions
{
    public bool Enabled { get; set; } = true;
    public bool OtlpEnabled { get; set; } = true;
    public bool ConsoleEnabled { get; set; }
    public bool IncludeFormattedMessage { get; set; } = true;
    public bool IncludeScopes { get; set; } = true;
    public bool ParseStateValues { get; set; } = true;
}
