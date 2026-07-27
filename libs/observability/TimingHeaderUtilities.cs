using System.Buffers.Text;
using System.Globalization;
using System.Text;

namespace ImagingPipeline.Observability;

internal static class TimingHeaderUtilities
{
    public static void SetCanonicalTimestamp(
        IDictionary<string, object?> headers,
        string headerName,
        long unixMilliseconds)
    {
        List<string>? variants = null;
        foreach (var key in headers.Keys)
        {
            if (!string.Equals(key, headerName, StringComparison.Ordinal)
                && string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
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

        headers[headerName] = unixMilliseconds;
    }

    public static bool TryReadTimestamp(
        IReadOnlyDictionary<string, object?>? headers,
        string headerName,
        bool acceptUnsignedIntegers,
        out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        if (headers is null)
        {
            return false;
        }

        if (!headers.TryGetValue(headerName, out var raw))
        {
            foreach (var pair in headers)
            {
                if (string.Equals(pair.Key, headerName, StringComparison.OrdinalIgnoreCase))
                {
                    raw = pair.Value;
                    break;
                }
            }
        }

        return TryReadValue(raw, acceptUnsignedIntegers, out unixMilliseconds);
    }

    public static bool TryReadValue(
        object? raw,
        bool acceptUnsignedIntegers,
        out long unixMilliseconds)
    {
        unixMilliseconds = 0;
        return raw switch
        {
            long value => Set(value, out unixMilliseconds),
            int value => Set(value, out unixMilliseconds),
            short value => Set(value, out unixMilliseconds),
            uint value when acceptUnsignedIntegers => Set(value, out unixMilliseconds),
            ulong value when acceptUnsignedIntegers && value <= long.MaxValue =>
                Set((long)value, out unixMilliseconds),
            string value => long.TryParse(
                value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out unixMilliseconds),
            byte[] value => TryParseUtf8(value, out unixMilliseconds),
            ReadOnlyMemory<byte> value => TryParseUtf8(value.Span, out unixMilliseconds),
            Memory<byte> value => TryParseUtf8(value.Span, out unixMilliseconds),
            _ => false
        };
    }

    public static bool ContainsHeader(
        IReadOnlyDictionary<string, object?>? headers,
        string headerName)
    {
        if (headers is null)
        {
            return false;
        }

        if (headers.ContainsKey(headerName))
        {
            return true;
        }

        foreach (var key in headers.Keys)
        {
            if (string.Equals(key, headerName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsPlausible(
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

    public static long ResolveMaximumAgeMilliseconds(
        TimeSpan? maximumAge,
        TimeSpan defaultMaximumAge)
    {
        var resolved = maximumAge ?? defaultMaximumAge;
        if (resolved <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumAge),
                "Maximum timing-header age must be positive.");
        }

        return (long)resolved.TotalMilliseconds;
    }

    public static long ResolveMaximumFutureClockSkewMilliseconds(
        TimeSpan? maximumFutureClockSkew,
        TimeSpan defaultMaximumFutureClockSkew)
    {
        var resolved = maximumFutureClockSkew ?? defaultMaximumFutureClockSkew;
        if (resolved < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumFutureClockSkew),
                "Maximum future clock skew must not be negative.");
        }

        return (long)resolved.TotalMilliseconds;
    }

    private static bool TryParseUtf8(ReadOnlySpan<byte> bytes, out long value)
    {
        if (Utf8Parser.TryParse(bytes, out value, out var bytesConsumed)
            && bytesConsumed == bytes.Length)
        {
            return true;
        }

        // Preserve NumberStyles.Integer behavior for uncommon externally supplied values
        // such as whitespace-padded timestamps. Canonical RabbitMQ headers take the
        // allocation-free Utf8Parser path above.
        return long.TryParse(
            Encoding.UTF8.GetString(bytes),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out value);
    }

    private static bool Set(long value, out long result)
    {
        result = value;
        return true;
    }
}
