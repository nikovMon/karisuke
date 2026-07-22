using System.Globalization;
using System.Text;

namespace ImagingPipeline.Observability;

public static class PipelineTimingHeaders
{
    public const string StartUnixMilliseconds = "x-pipeline-start-unix-ms";
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
        var maximumAgeMilliseconds = ResolveMaximumAgeMilliseconds(maximumAge);
        var maximumFutureClockSkewMilliseconds =
            ResolveMaximumFutureClockSkewMilliseconds(maximumFutureClockSkew);

        // Messages produced by this library already use the canonical key and a positive
        // integer value. Keep that full-rate path allocation-free.
        if (headers.TryGetValue(StartUnixMilliseconds, out var canonicalValue)
            && TryReadValue(canonicalValue, out var canonicalOrigin)
            && IsPlausible(
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
            if (existingOrigin is null && TryReadValue(headers[key], out var parsedOrigin))
            {
                if (IsPlausible(
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
            if (ContainsHeader(headers))
            {
                PipelineTelemetry.RecordInvalidTimingHeader(
                    TimingHeaderKind.PipelineOrigin,
                    TimingHeaderRejectionReason.Malformed);
            }

            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        if (!IsPlausible(
                startedAt,
                now,
                ResolveMaximumAgeMilliseconds(maximumAge),
                ResolveMaximumFutureClockSkewMilliseconds(maximumFutureClockSkew),
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
    {
        unixMilliseconds = 0;
        if (headers is null)
        {
            return false;
        }

        object? raw = null;
        if (!headers.TryGetValue(StartUnixMilliseconds, out raw))
        {
            raw = headers.FirstOrDefault(pair =>
                    string.Equals(pair.Key, StartUnixMilliseconds, StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        return TryReadValue(raw, out unixMilliseconds);
    }

    private static bool TryReadValue(object? raw, out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        return raw switch
        {
            long value => Set(value, out unixMilliseconds),
            int value => Set(value, out unixMilliseconds),
            short value => Set(value, out unixMilliseconds),
            string value => long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out unixMilliseconds),
            byte[] value => TryParseBytes(value, out unixMilliseconds),
            ReadOnlyMemory<byte> value => TryParseBytes(value.Span, out unixMilliseconds),
            Memory<byte> value => TryParseBytes(value.Span, out unixMilliseconds),
            _ => false
        };
    }

    private static bool TryParseBytes(ReadOnlySpan<byte> bytes, out long value) =>
        long.TryParse(Encoding.UTF8.GetString(bytes), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool Set(long value, out long result)
    {
        result = value;
        return true;
    }

    private static bool ContainsHeader(IReadOnlyDictionary<string, object?>? headers) =>
        headers is not null &&
        (headers.ContainsKey(StartUnixMilliseconds) || headers.Keys.Any(static key =>
            string.Equals(key, StartUnixMilliseconds, StringComparison.OrdinalIgnoreCase)));

    private static bool IsPlausible(
        long timestamp,
        long now,
        long maximumAgeMilliseconds,
        long maximumFutureClockSkewMilliseconds,
        out TimingHeaderRejectionReason rejectionReason)
    {
        if (timestamp <= 0)
        {
            rejectionReason = TimingHeaderRejectionReason.Malformed;
            return false;
        }

        if (timestamp > now && timestamp - now > maximumFutureClockSkewMilliseconds)
        {
            rejectionReason = TimingHeaderRejectionReason.Future;
            return false;
        }

        if (now - timestamp > maximumAgeMilliseconds)
        {
            rejectionReason = TimingHeaderRejectionReason.TooOld;
            return false;
        }

        rejectionReason = default;
        return true;
    }

    private static long ResolveMaximumAgeMilliseconds(TimeSpan? maximumAge)
    {
        var resolved = maximumAge ?? DefaultMaximumAge;
        if (resolved <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumAge), "Maximum timing-header age must be positive.");
        }

        return (long)resolved.TotalMilliseconds;
    }

    private static long ResolveMaximumFutureClockSkewMilliseconds(TimeSpan? maximumFutureClockSkew)
    {
        var resolved = maximumFutureClockSkew ?? DefaultMaximumFutureClockSkew;
        if (resolved < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFutureClockSkew),
                "Maximum future clock skew must not be negative.");
        }

        return (long)resolved.TotalMilliseconds;
    }
}
