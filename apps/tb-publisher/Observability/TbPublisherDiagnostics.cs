using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace ImagingPipeline.TbPublisher.Observability;

public static class TbPublisherDiagnostics
{
    public const string ActivitySourceName = "ImagingPipeline.TbPublisher";
    public const string MeterName = "ImagingPipeline.TbPublisher";

    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagesValidated =
        Meter.CreateCounter<long>("imagingpipeline.tbpublisher.messages.validated");

    public static readonly Counter<long> ValidationFailures =
        Meter.CreateCounter<long>("imagingpipeline.tbpublisher.messages.validation_failures");

    public static readonly Counter<long> TilingConfigMappingFailures =
        Meter.CreateCounter<long>("imagingpipeline.tbpublisher.tiling_config.mapping_failures");

    public static readonly Counter<long> MessagesPublishedToTilingConfig =
        Meter.CreateCounter<long>("imagingpipeline.tbpublisher.tiling_config.messages_published");
}
