namespace ImagingPipeline.Observability;

public static class PipelineTimingHeaders
{
    public const string StartUnixMilliseconds = "findair-started-at-unix-ms";
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultMaximumFutureClockSkew = TimeSpan.FromSeconds(5);

    public static void EnsureStarted(
        IDictionary<string, object?> headers,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumAge = null,
        TimeSpan? maximumFutureClockSkew = null)
    {
        ArgumentNullException.ThrowIfNull(headers);
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        var maximumAgeMilliseconds =
            TimingHeaderUtilities.ResolveMaximumAgeMilliseconds(maximumAge, DefaultMaximumAge);
        var maximumFutureClockSkewMilliseconds =
            TimingHeaderUtilities.ResolveMaximumFutureClockSkewMilliseconds(
                maximumFutureClockSkew,
                DefaultMaximumFutureClockSkew);

        // Messages produced by this library already use the canonical key and a positive
        // integer value. Keep that full-rate path allocation-free.
        if (headers.TryGetValue(StartUnixMilliseconds, out var canonicalValue)
            && TimingHeaderUtilities.TryReadValue(
                canonicalValue,
                acceptUnsignedIntegers: false,
                out var canonicalOrigin)
            && TimingHeaderUtilities.IsPlausible(
                canonicalOrigin,
                now,
                maximumAgeMilliseconds,
                maximumFutureClockSkewMilliseconds,
                out _)
            && !headers.Keys.Any(static key =>
                !string.Equals(key, StartUnixMilliseconds, StringComparison.Ordinal)
                && string.Equals(key, StartUnixMilliseconds, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var hasMatchingKey = headers.Keys.Any(static key => string.Equals(
            key,
            StartUnixMilliseconds,
            StringComparison.OrdinalIgnoreCase));
        if (!hasMatchingKey)
        {
            headers[StartUnixMilliseconds] = now;
            return;
        }

        long? existingOrigin = null;
        TimingHeaderRejectionReason? rejectionReason = null;
        var matchingKeys = headers.Keys
            .Where(static key => string.Equals(
                key,
                StartUnixMilliseconds,
                StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static key =>
                string.Equals(key, StartUnixMilliseconds, StringComparison.Ordinal))
            .ToArray();

        foreach (var key in matchingKeys)
        {
            if (existingOrigin is null
                && TimingHeaderUtilities.TryReadValue(
                    headers[key],
                    acceptUnsignedIntegers: false,
                    out var parsedOrigin))
            {
                if (TimingHeaderUtilities.IsPlausible(
                        parsedOrigin,
                        now,
                        maximumAgeMilliseconds,
                        maximumFutureClockSkewMilliseconds,
                        out var reason))
                {
                    existingOrigin = parsedOrigin;
                }
                else
                {
                    rejectionReason ??= reason;
                }
            }
            else if (existingOrigin is null)
            {
                rejectionReason ??= TimingHeaderRejectionReason.Malformed;
            }

            headers.Remove(key);
        }

        headers[StartUnixMilliseconds] =
            existingOrigin
            ?? now;
        if (rejectionReason.HasValue)
        {
            PipelineTelemetry.RecordInvalidTimingHeader(
                TimingHeaderKind.PipelineOrigin,
                rejectionReason.Value);
        }
    }

    public static bool TryGetElapsedSeconds(
        IReadOnlyDictionary<string, object?>? headers,
        out double elapsedSeconds,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumAge = null,
        TimeSpan? maximumFutureClockSkew = null)
    {
        elapsedSeconds = 0;
        if (!TryReadStartUnixMilliseconds(headers, out var startedAt))
        {
            if (TimingHeaderUtilities.ContainsHeader(headers, StartUnixMilliseconds))
            {
                PipelineTelemetry.RecordInvalidTimingHeader(
                    TimingHeaderKind.PipelineOrigin,
                    TimingHeaderRejectionReason.Malformed);
            }

            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        if (!TimingHeaderUtilities.IsPlausible(
                startedAt,
                now,
                TimingHeaderUtilities.ResolveMaximumAgeMilliseconds(maximumAge, DefaultMaximumAge),
                TimingHeaderUtilities.ResolveMaximumFutureClockSkewMilliseconds(
                    maximumFutureClockSkew,
                    DefaultMaximumFutureClockSkew),
                out var rejectionReason))
        {
            PipelineTelemetry.RecordInvalidTimingHeader(
                TimingHeaderKind.PipelineOrigin,
                rejectionReason);
            return false;
        }

        elapsedSeconds = Math.Max(0, now - startedAt) / 1_000d;
        return true;
    }

    public static bool TryReadStartUnixMilliseconds(
        IReadOnlyDictionary<string, object?>? headers,
        out long unixMilliseconds)
        => TimingHeaderUtilities.TryReadTimestamp(
            headers,
            StartUnixMilliseconds,
            acceptUnsignedIntegers: false,
            out unixMilliseconds);

}
