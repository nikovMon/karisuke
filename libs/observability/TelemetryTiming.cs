using System.Diagnostics;

namespace ImagingPipeline.Observability;

public static class TelemetryTiming
{
    public static long StartTimestamp() => Stopwatch.GetTimestamp();

    public static double ElapsedSeconds(long startTimestamp) =>
        Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds;
}
