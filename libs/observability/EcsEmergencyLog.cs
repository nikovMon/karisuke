namespace ImagingPipeline.Observability;

internal static class EcsEmergencyLog
{
    private static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(1);
    private static long _nextWriteUnixMilliseconds;

    public static void Write(string message)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var next = Volatile.Read(ref _nextWriteUnixMilliseconds);
        if (now < next
            || Interlocked.CompareExchange(
                ref _nextWriteUnixMilliseconds,
                now + (long)MinimumInterval.TotalMilliseconds,
                next) != next)
        {
            return;
        }

        try
        {
            Console.Error.WriteLine(
                $"{DateTimeOffset.UtcNow:O} observability-emergency: {message}");
        }
        catch
        {
            // Logging must never fail a business operation.
        }
    }
}
