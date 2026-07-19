using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.ProjectionMapperClient;

public static class ProjectionMapperClientDiagnostics
{
    public const string ActivitySourceName = "ImagingPipeline.ProjectionMapperClient";
    public const string MeterName = "ImagingPipeline.ProjectionMapperClient";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> Calls =
        Meter.CreateCounter<long>("imagingpipeline.projection_mapper.calls");

    public static readonly Counter<long> Failures =
        Meter.CreateCounter<long>("imagingpipeline.projection_mapper.failures");

    public static readonly Histogram<double> DurationMs =
        Meter.CreateHistogram<double>("imagingpipeline.projection_mapper.duration", "ms");
}
