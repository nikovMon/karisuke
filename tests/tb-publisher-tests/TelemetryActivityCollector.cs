using System.Collections.Concurrent;
using System.Diagnostics;

namespace ImagingPipeline.TbPublisher.Tests;

internal sealed class TelemetryActivityCollector : IDisposable
{
    private readonly ConcurrentQueue<Activity> _activities = new();
    private readonly ActivityListener _listener;

    public TelemetryActivityCollector(string sourceName)
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = source => string.Equals(source.Name, sourceName, StringComparison.Ordinal),
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => _activities.Enqueue(activity)
        };
        ActivitySource.AddActivityListener(_listener);
    }

    public IReadOnlyList<Activity> Activities => _activities.ToArray();

    public void Dispose() => _listener.Dispose();
}
