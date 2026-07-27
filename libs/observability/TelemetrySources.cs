using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Reflection;

namespace ImagingPipeline.Observability;

public static class TelemetrySources
{
    private static readonly string InstrumentationVersion =
        typeof(TelemetrySources).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

    public static readonly ActivitySource Observability = CreateActivitySource(TelemetrySourceNames.Observability);
    public static readonly ActivitySource RabbitMq = CreateActivitySource(TelemetrySourceNames.RabbitMq);
    public static readonly ActivitySource ProjectionMapper = CreateActivitySource(TelemetrySourceNames.ProjectionMapper);
    public static readonly ActivitySource Elasticsearch = CreateActivitySource(TelemetrySourceNames.Elasticsearch);
    public static readonly ActivitySource Dependencies = CreateActivitySource(TelemetrySourceNames.Dependencies);
    public static readonly ActivitySource Pipeline = CreateActivitySource(TelemetrySourceNames.Pipeline);
    public static readonly ActivitySource RulesApi = CreateActivitySource(TelemetrySourceNames.RulesApi);
    public static readonly ActivitySource Gateway = CreateActivitySource(TelemetrySourceNames.Gateway);
    public static readonly ActivitySource TbPublisher = CreateActivitySource(TelemetrySourceNames.TbPublisher);
    public static readonly ActivitySource TileBuilder = CreateActivitySource(TelemetrySourceNames.TileBuilder);
    public static readonly ActivitySource TbConsumer = CreateActivitySource(TelemetrySourceNames.TbConsumer);
    public static readonly ActivitySource Embedder = CreateActivitySource(TelemetrySourceNames.Embedder);

    private static ActivitySource CreateActivitySource(string name) => new(name, InstrumentationVersion);
}

public static class TelemetryMeters
{
    private static readonly string InstrumentationVersion =
        typeof(TelemetryMeters).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ?? "1.0.0";

    public static readonly Meter RabbitMq = new(TelemetrySourceNames.RabbitMq, InstrumentationVersion);
    public static readonly Meter Dependencies = new(TelemetrySourceNames.Dependencies, InstrumentationVersion);
    public static readonly Meter Pipeline = new(TelemetrySourceNames.Pipeline, InstrumentationVersion);
    public static readonly Meter Gateway = new(TelemetrySourceNames.Gateway, InstrumentationVersion);
    public static readonly Meter RulesApi = new(TelemetrySourceNames.RulesApi, InstrumentationVersion);
}
