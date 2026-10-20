using System.Globalization;
using System.Text.RegularExpressions;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

internal static partial class ProviderDates
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex OffsetTimestampPattern();

    internal static bool TryExplicitOffset(string? value, out DateTimeOffset result)
    {
        result = default;
        return value is not null && OffsetTimestampPattern().IsMatch(value) &&
               DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }

    internal static DateTimeOffset Parse(string value, TimeZoneInfo sourceZone)
    {
        if (TryExplicitOffset(value, out var timestamp))
            return timestamp.ToUniversalTime();

        string[] formats = ["dd/MM/yyyy HH:mm:ss", "dd/MM/yyyy HH:mm:ss.FFFFFFF",
            "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF"];
        if (!DateTime.TryParseExact(value, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new FormatException("Timestamp must be ISO 8601 or dd/MM/yyyy HH:mm:ss.");

        local = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        if (sourceZone.IsInvalidTime(local) || sourceZone.IsAmbiguousTime(local))
            throw new FormatException("Timestamp falls in an invalid or ambiguous daylight-saving local time.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, sourceZone));
    }
}
