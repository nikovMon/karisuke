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
        TimingHeaderUtilities.SetCanonicalTimestamp(
            headers,
            PublishedUnixMilliseconds,
            publishedAt);
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
            if (TimingHeaderUtilities.ContainsHeader(headers, PublishedUnixMilliseconds))
            {
                PipelineTelemetry.RecordInvalidTimingHeader(
                    TimingHeaderKind.MessagePublished,
                    TimingHeaderRejectionReason.Malformed);
            }

            return false;
        }

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds();
        if (!TimingHeaderUtilities.IsPlausible(
                publishedAt,
                now,
                TimingHeaderUtilities.ResolveMaximumAgeMilliseconds(maximumAge, DefaultMaximumAge),
                TimingHeaderUtilities.ResolveMaximumFutureClockSkewMilliseconds(
                    maximumFutureClockSkew,
                    DefaultMaximumFutureClockSkew),
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
        => TimingHeaderUtilities.TryReadTimestamp(
            headers,
            PublishedUnixMilliseconds,
            acceptUnsignedIntegers: true,
            out unixMilliseconds);
}
