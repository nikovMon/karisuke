using System.Globalization;
using System.Text;

namespace ImagingPipeline.Observability;

/// <summary>
/// Carries a per-hop publish timestamp used to measure broker delivery delay.
/// Unlike <see cref="PipelineTimingHeaders"/>, this value is replaced before every publish.
/// </summary>
public static class MessagingTimingHeaders
{
    public const string PublishedUnixMilliseconds = "x-pipeline-published-unix-ms";
    public static readonly TimeSpan DefaultMaximumAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan DefaultMaximumFutureClockSkew = TimeSpan.FromSeconds(5);

    public static void StampPublished(
        IDictionary<string, object?> headers,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(headers);

        var publishedAt =
            (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        List<string>? variants = null;
        foreach (var key in headers.Keys)
        {
            if (!string.Equals(key, PublishedUnixMilliseconds, StringComparison.Ordinal)
                && string.Equals(key, PublishedUnixMilliseconds, StringComparison.OrdinalIgnoreCase))
            {
                (variants ??= []).Add(key);
            }
        }

        if (variants is not null)
        {
            foreach (var key in variants)
            {
                headers.Remove(key);
            }
        }

        headers[PublishedUnixMilliseconds] = publishedAt;
    }

    public static bool TryGetDeliveryDelaySeconds(
        IReadOnlyDictionary<string, object?>? headers,
        out double delaySeconds,
        TimeProvider? timeProvider = null,
        TimeSpan? maximumAge = null,
        TimeSpan? maximumFutureClockSkew = null)
    {
        delaySeconds = 0;
        if (!TryReadPublishedUnixMilliseconds(headers, out var publishedAt))
        {
            if (ContainsHeader(headers))
            {
                PipelineTelemetry.RecordInvalidTimingHeader(
                    TimingHeaderKind.MessagePublished,
                    TimingHeaderRejectionReason.Malformed);
            }

            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        if (!IsPlausible(
                publishedAt,
                now,
                ResolveMaximumAgeMilliseconds(maximumAge),
                ResolveMaximumFutureClockSkewMilliseconds(maximumFutureClockSkew),
                out var rejectionReason))
        {
            PipelineTelemetry.RecordInvalidTimingHeader(
                TimingHeaderKind.MessagePublished,
                rejectionReason);
            return false;
        }

        delaySeconds = Math.Max(0, now - publishedAt) / 1_000d;
        return true;
    }

    public static bool TryReadPublishedUnixMilliseconds(
        IReadOnlyDictionary<string, object?>? headers,
        out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (headers is null)
        {
            return false;
        }

        object? raw = null;
        if (!headers.TryGetValue(PublishedUnixMilliseconds, out raw))
        {
            raw = headers.FirstOrDefault(static pair => string.Equals(
                    pair.Key,
                    PublishedUnixMilliseconds,
                    StringComparison.OrdinalIgnoreCase))
                .Value;
        }

        return raw switch
        {
            long value => Set(value, out unixMilliseconds),
            int value => Set(value, out unixMilliseconds),
            short value => Set(value, out unixMilliseconds),
            uint value => Set(value, out unixMilliseconds),
            ulong value when value <= long.MaxValue => Set((long)value, out unixMilliseconds),
            string value => long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out unixMilliseconds),
            byte[] value => TryParseBytes(value, out unixMilliseconds),
            ReadOnlyMemory<byte> value => TryParseBytes(value.Span, out unixMilliseconds),
            Memory<byte> value => TryParseBytes(value.Span, out unixMilliseconds),
            _ => false
        };
    }

    private static bool TryParseBytes(ReadOnlySpan<byte> bytes, out long value) =>
        long.TryParse(
            Encoding.UTF8.GetString(bytes),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);

    private static bool Set(long value, out long result)
    {
        result = value;
        return true;
    }

    private static bool ContainsHeader(IReadOnlyDictionary<string, object?>? headers) =>
        headers is not null &&
        (headers.ContainsKey(PublishedUnixMilliseconds) || headers.Keys.Any(static key =>
            string.Equals(key, PublishedUnixMilliseconds, StringComparison.OrdinalIgnoreCase)));

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
