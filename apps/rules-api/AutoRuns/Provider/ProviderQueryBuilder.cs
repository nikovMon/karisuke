using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using ImagingPipeline.Common.Dtos.Rules.Models;
using ImagingPipeline.GeometryUtils;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

/// <summary>
/// Uses only the verified AND grammar. Separate sensor branches keep each sensor's qualities
/// and grid types together; callers union pages locally instead of guessing provider OR syntax.
/// </summary>
public sealed class ProviderQueryBuilder(IOptions<ElasticProviderOptions> configured)
{
    private readonly ElasticProviderOptions _options = configured.Value;
    private static readonly ConditionalWeakTable<AutoRunFilters, CompiledFilter> FilterCache = new();

    public IReadOnlyList<JsonElement> Build(AutoRunSource source, IReadOnlyList<PipelineRuleDocument> rules)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(rules);
        if (source.Filters is not null)
        {
            var errors = Validate(source.Filters).ToArray();
            if (errors.Length != 0) throw new ArgumentException(string.Join(" ", errors), nameof(source));
        }
        if (source.Mode == AutoRunSourceMode.OverlayIds) return [];
        var result = new List<JsonElement>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (source.Mode == AutoRunSourceMode.Filters)
            AddQuery(FilterClauses(source.Filters));
        else if (source.Mode == AutoRunSourceMode.Rules)
        {
            foreach (var rule in rules)
            {
                var common = FilterClauses(source.Filters);
                var match = rule.Match;
                if (match?.Resolution is { } resolution)
                    AddRange(common, _options.RequestFields.Resolution, Number(resolution.Min), Number(resolution.Max));
                if (match?.LocationWkt is { } area)
                    common.Add(Clause("geo_intersect", _options.RequestFields.Geometry, new object[] { area }));
                if (match?.ModeSar is { Count: > 0 } modes && !string.IsNullOrWhiteSpace(_options.RequestFields.ModeSar))
                    common.Add(Clause("match", _options.RequestFields.ModeSar, modes.ToArray()));
                // MaxPhotoAgeDays is intentionally not pushed down for historical execution.
                if (match?.Sensors is { Count: > 0 } sensors)
                {
                    foreach (var sensor in sensors)
                    {
                        var branch = new List<object>(common)
                        {
                            Clause("match", _options.RequestFields.SensorName, new[] { sensor.Name })
                        };
                        if (sensor.RegistrationQualities is { Count: > 0 } qualities)
                            branch.Add(Clause("match", _options.RequestFields.RegistrationQuality,
                                qualities.Select(value => value.ToJsonValue()).ToArray()));
                        if (sensor.GridTypes is { Count: > 0 } grids)
                            branch.Add(Clause("match", _options.RequestFields.GridType, grids.ToArray()));
                        AddQuery(branch);
                    }
                }
                else AddQuery(common);
            }
        }
        else throw new ArgumentException("Unknown auto-run source mode.", nameof(source));
        return result;

        void AddQuery(List<object> clauses)
        {
            var query = JsonSerializer.SerializeToElement(new
            {
                filter = new { logical_operators = new Dictionary<string, object> { ["AND"] = clauses } },
                sort = new Dictionary<string, string> { [Field(_options.RequestFields.SortDate)] = "desc" }
            });
            if (seen.Add(query.GetRawText())) result.Add(query);
        }
    }

    public static IEnumerable<string> Validate(AutoRunFilters filters)
    {
        ArgumentNullException.ThrowIfNull(filters);
        foreach (var (name, values) in new (string, string[]?)[]
        {
            ("sensorNames", filters.SensorNames), ("sensorTypes", filters.SensorTypes),
            ("gridTypes", filters.GridTypes), ("registrationQualities", filters.RegistrationQualities),
            ("sortieIds", filters.SortieIds)
        })
        {
            if (values is { Length: 0 }) yield return $"{name} cannot be empty; omit it for no constraint.";
            if (values?.Any(string.IsNullOrWhiteSpace) == true) yield return $"{name} contains a blank value.";
        }
        if (filters.RegistrationQualities?.Any(value =>
                !RegistrationQualityExtensions.TryParseJsonValue(value?.Trim(), out _)) == true)
            yield return $"registrationQualities must contain {RegistrationQualityContract.AllowedJsonValues}.";
        if (filters.Resolution is { } resolution)
        {
            if (resolution.Min is null && resolution.Max is null)
                yield return "resolution must supply at least one bound.";
            if (resolution.Min is { } min && (!double.IsFinite(min) || min <= 0) ||
                resolution.Max is { } max && (!double.IsFinite(max) || max <= 0))
                yield return "resolution bounds must be positive finite numbers.";
            if (resolution.Min > resolution.Max) yield return "resolution.min must not exceed resolution.max.";
        }
        foreach (var (name, range) in new[] { ("createdTime", filters.CreatedTime), ("photoTime", filters.PhotoTime) })
        {
            if (range is null) continue;
            if (range.From is null && range.To is null) yield return $"{name} must supply at least one bound.";
            var validFrom = ProviderDates.TryExplicitOffset(range.From, out var from);
            var validTo = ProviderDates.TryExplicitOffset(range.To, out var to);
            if (range.From is not null && !validFrom || range.To is not null && !validTo)
                yield return $"{name} bounds must be ISO 8601 timestamps with explicit UTC offsets.";
            if (validFrom && validTo && from > to) yield return $"{name}.from must not exceed {name}.to.";
        }
        if (filters.Wkt is not null)
        {
            string? error = null;
            try
            {
                if (string.IsNullOrWhiteSpace(filters.Wkt) ||
                    GeometryUtilities.ReadWkt(filters.Wkt) is not (Polygon or MultiPolygon))
                    error = "wkt must be a nonempty valid Polygon or MultiPolygon.";
            }
            catch (Exception ex) when (ex is ArgumentException or ParseException or GeometryValidationException)
            { error = "wkt must be a nonempty valid Polygon or MultiPolygon."; }
            if (error is not null) yield return error;
        }
    }

    /// <summary>Rechecks every explicit filter locally, including false booleans and inclusive ranges.</summary>
    public static bool Matches(ProviderOverlay overlay, AutoRunFilters filters) =>
        FilterCache.GetValue(filters, static value => new CompiledFilter(value)).Matches(overlay);

    private List<object> FilterClauses(AutoRunFilters? filters)
    {
        var clauses = new List<object>();
        if (filters is null) return clauses;
        var fields = _options.RequestFields;
        AddList(fields.SensorName, filters.SensorNames);
        AddList(fields.SensorType, filters.SensorTypes);
        AddList(fields.GridType, filters.GridTypes);
        AddList(fields.RegistrationQuality, filters.RegistrationQualities);
        AddList(fields.SortieId, filters.SortieIds);
        if (filters.IsRealtime is { } realtime)
            clauses.Add(Clause("match", fields.IsRealtime, new[]
            {
                _options.StringEncodedBooleans ? (object)(realtime ? "true" : "false") : realtime
            }));
        if (filters.Resolution is { } resolution)
            AddRange(clauses, fields.Resolution, Number(resolution.Min), Number(resolution.Max));
        AddTime(fields.CreatedTime, filters.CreatedTime);
        AddTime(fields.PhotoTime, filters.PhotoTime);
        if (filters.Wkt is { } area)
            clauses.Add(Clause("geo_intersect", fields.Geometry, new[] { area }));
        return clauses;

        void AddList(string field, string[]? values)
        {
            if (values is not null)
                clauses.Add(Clause("match", field, values.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).ToArray()));
        }

        void AddTime(string field, AutoRunTimeRange? range)
        {
            if (range is not null)
                AddRange(clauses, field, Timestamp(range.From), Timestamp(range.To));
        }
    }

    private object? Number(double? value) => value is null ? null :
        _options.StringEncodedNumbers ? value.Value.ToString("R", CultureInfo.InvariantCulture) : value.Value;

    private static string? Timestamp(string? value) => value is null ? null :
        ProviderDates.TryExplicitOffset(value, out var time)
            ? time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : throw new ArgumentException("Time ranges require explicit UTC offsets.");

    private static object Clause(string operation, string field, object values) =>
        new Dictionary<string, object>
        {
            [operation] = new Dictionary<string, object>
            {
                [Field(field)] = new { type = "IN", values }
            }
        };

    private static void AddRange(List<object> clauses, string field, object? from, object? to)
    {
        var bounds = new Dictionary<string, object>();
        if (from is not null) bounds.Add("gte", from);
        if (to is not null) bounds.Add("lte", to);
        if (bounds.Count > 0) clauses.Add(Clause("range", field, new[] { bounds }));
    }

    private static string Field(string field) => !string.IsNullOrWhiteSpace(field)
        ? field : throw new ElasticProviderException("Provider request field aliases must be nonempty.");

    private sealed class CompiledFilter
    {
        private readonly HashSet<string>? _sensors;
        private readonly HashSet<string>? _types;
        private readonly HashSet<string>? _grids;
        private readonly HashSet<string>? _qualities;
        private readonly HashSet<string>? _sorties;
        private readonly bool? _realtime;
        private readonly double? _minimum;
        private readonly double? _maximum;
        private readonly DateTimeOffset? _createdFrom;
        private readonly DateTimeOffset? _createdTo;
        private readonly DateTimeOffset? _photoFrom;
        private readonly DateTimeOffset? _photoTo;
        private readonly Geometry? _area;

        public CompiledFilter(AutoRunFilters filters)
        {
            var errors = Validate(filters).ToArray();
            if (errors.Length > 0) throw new ArgumentException(string.Join(" ", errors), nameof(filters));
            _sensors = Set(filters.SensorNames); _types = Set(filters.SensorTypes); _grids = Set(filters.GridTypes);
            _qualities = Set(filters.RegistrationQualities); _sorties = Set(filters.SortieIds);
            _realtime = filters.IsRealtime;
            _minimum = filters.Resolution?.Min; _maximum = filters.Resolution?.Max;
            _createdFrom = Time(filters.CreatedTime?.From); _createdTo = Time(filters.CreatedTime?.To);
            _photoFrom = Time(filters.PhotoTime?.From); _photoTo = Time(filters.PhotoTime?.To);
            _area = filters.Wkt is null ? null : GeometryUtilities.ReadWkt(filters.Wkt);
        }

        public bool Matches(ProviderOverlay overlay)
        {
            var input = overlay.Input;
            return In(_sensors, input.SensorName) && In(_types, input.SensorType) && In(_grids, input.GridType) &&
                   In(_qualities, input.RegistrationQuality.ToJsonValue()) && In(_sorties, overlay.SortieId) &&
                   (_realtime is null || overlay.IsRealtime == _realtime) &&
                   (_minimum is null || input.BestResolution >= _minimum) &&
                   (_maximum is null || input.BestResolution <= _maximum) &&
                   InTime(overlay.CreatedTime, _createdFrom, _createdTo) &&
                   InTime(input.PhotoTime, _photoFrom, _photoTo) &&
                   (_area is null || _area.Intersects(input.Geometry));
        }

        private static HashSet<string>? Set(string[]? values) =>
            values is null ? null : new(values.Select(value => value.Trim()), StringComparer.Ordinal);
        private static bool In(HashSet<string>? allowed, string? value) => allowed is null || value is not null && allowed.Contains(value);
        private static DateTimeOffset? Time(string? value) =>
            ProviderDates.TryExplicitOffset(value, out var time) ? time : null;
        private static bool InTime(DateTimeOffset? value, DateTimeOffset? from, DateTimeOffset? to) =>
            from is null && to is null || value is not null &&
            (from is null || value.Value >= from.Value) && (to is null || value.Value <= to.Value);
    }
}
