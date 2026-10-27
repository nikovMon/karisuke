using System.Buffers;
using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.RuleEngine.Input;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public sealed partial class ElasticOverlayProvider(HttpClient client, IOptions<ElasticProviderOptions> configured,
    IAutoRunMemoryGuard? memory = null, TimeProvider? timeProvider = null, ILogger<ElasticOverlayProvider>? logger = null,
    ProviderRequestCoordinator? coordinator = null)
    : IElasticOverlayProvider
{
    private const string OffsetlessResponseTimeZone = "Asia/Jerusalem";
    private readonly ElasticProviderOptions _options = configured.Value;
    private readonly IReadOnlyDictionary<string, string> _headers = ElasticProviderHeaders.Snapshot(configured.Value.Headers);

    public async Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var uri = WithPageLimit(new Uri(TableUri().AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(id)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var document = await SendAsync(request, "overlay lookup", allowNotFound: true, cancellationToken, report);
        if (document is null) return new(null, "Overlay was not found by Elastic Provider.");
        memory?.ThrowIfUnsafe();
        var result = Map(document.RootElement);
        return result.Overlay is { } overlay && !string.Equals(overlay.Input.ImageId, id, StringComparison.Ordinal)
            ? new(null, "Provider returned an overlay ID different from the requested ID.")
            : result;
    }

    public Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null) => SearchPageCoreAsync(query, nextPage, null, cancellationToken, report);

    public Task<ProviderPage> VisitPageAsync(JsonElement query, string? nextPage,
        Func<ProviderOverlayResult, ValueTask<bool>> accept, CancellationToken cancellationToken,
        Action<ProviderProgress>? report = null) => SearchPageCoreAsync(query, nextPage, accept, cancellationToken, report);

    private async Task<ProviderPage> SearchPageCoreAsync(JsonElement query, string? nextPage,
        Func<ProviderOverlayResult, ValueTask<bool>>? accept, CancellationToken cancellationToken,
        Action<ProviderProgress>? report)
    {
        if (query.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Provider query must be an object.", nameof(query));
        var uri = nextPage is null ? WithPageLimit(TableUri()) : ContinuationUri(nextPage);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(query, mediaType: new MediaTypeHeaderValue("application/json-patch+json"))
        };
        using var document = await SendAsync(request, nextPage is null ? "initial search page" : "continuation page",
            allowNotFound: false, cancellationToken, report)
            ?? throw new ElasticProviderException("Provider returned no search response.");
        var root = document.RootElement;
        RequireObject(root, "Batch response");
        if (!root.TryGetProperty("entities_list", out var entities) || entities.ValueKind != JsonValueKind.Array)
            throw new ElasticProviderException("Provider batch response must contain an entities_list array.");
        // An empty page is terminal; do not inspect an unused continuation URL.
        if (entities.GetArrayLength() == 0) return new([], null);

        string? continuation = null;
        if (root.TryGetProperty("next_page", out var next) && next.ValueKind != JsonValueKind.Null)
        {
            if (next.ValueKind != JsonValueKind.String)
                throw new ElasticProviderException("Provider next_page must be a URL string or null.");
            var value = next.GetString();
            if (!string.IsNullOrWhiteSpace(value))
                continuation = ContinuationUri(value).AbsoluteUri;
        }

        var items = new List<ProviderOverlayResult>();
        foreach (var entity in entities.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (memory?.CanAllocate() == false) return new(items, null, "memoryLimit");
            var item = Map(entity);
            if (accept is null) items.Add(item);
            else if (!await accept(item)) return new([], null);
        }
        return new(items, continuation);
    }

    public async Task<ProviderOverlayResult> ResolveSensorTypeAsync(ProviderOverlay overlay,
        CancellationToken cancellationToken, Action<ProviderProgress>? report = null)
    {
        ArgumentNullException.ThrowIfNull(overlay);
        var id = overlay.Input.ImageId;
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var uri = WithPageLimit(new Uri(TableUri().AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(id)));
        uri = new UriBuilder(uri) { Query = uri.Query.TrimStart('?') + "&result_type=4" }.Uri;
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        JsonDocument? rawDocument;
        try { rawDocument = await SendAsync(request, "sensor type lookup", allowNotFound: true, cancellationToken, report); }
        catch (ElasticProviderException ex) when (ex.Diagnostic?.Code == "providerInvalidJson")
        { return new(null, "Raw provider overlay response is invalid JSON while resolving sensorType."); }
        using var document = rawDocument;
        if (document is null) return new(null, "Raw provider overlay was not found while resolving sensorType.");
        memory?.ThrowIfUnsafe();
        try
        {
            var root = document.RootElement;
            RequireObject(root, "Raw provider overlay");
            if (!string.Equals(RequiredString(root, "id"), id, StringComparison.Ordinal))
                return new(null, "Raw provider overlay ID differs from the requested ID while resolving sensorType.");
            var sensorType = RequiredString(root, "sensorType");
            using var payload = JsonDocument.Parse(overlay.Payload);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                foreach (var property in payload.RootElement.EnumerateObject())
                    if (!property.NameEquals("sensorType")) property.WriteTo(writer);
                writer.WriteString("sensorType", sensorType);
                writer.WriteEndObject();
            }
            return new(overlay with
            {
                Payload = buffer.WrittenSpan.ToArray(),
                Input = overlay.Input with { SensorType = sensorType }
            }, null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ElasticProviderException)
        {
            return new(null, $"Invalid raw provider overlay while resolving sensorType: {ex.Message}");
        }
    }

    private ProviderOverlayResult Map(JsonElement entity)
    {
        var zone = SourceZone();
        try
        {
            RequireObject(entity, "Overlay");
            if (!entity.TryGetProperty("properties_list", out var properties))
                throw new FormatException("Overlay properties_list is missing.");
            RequireObject(properties, "Overlay properties_list");
            if (!entity.TryGetProperty("geo", out var geo))
                throw new FormatException("Overlay geo is missing.");
            RequireObject(geo, "Overlay geo");
            var geometry = GeometryUtilities.ReadWkt(RequiredString(geo, "wkt"));
            if (geometry is not Polygon and not MultiPolygon)
                throw new FormatException("Overlay geo.wkt must be a Polygon or MultiPolygon.");

            var fields = _options.ResponseFields;
            var photoTime = ProviderDates.Parse(RequiredString(properties, fields.PhotoTime), zone);
            var createdText = OptionalString(properties, fields.CreatedTime);
            DateTimeOffset? created = createdText is null ? null : ProviderDates.Parse(createdText, zone);
            var realtime = OptionalBoolean(properties, fields.IsRealtime);
            var sortie = OptionalString(properties, fields.SortieId);
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("id", RequiredString(properties, fields.Id));
                writer.WriteString("sensorName", RequiredString(properties, fields.SensorName));
                writer.WriteString("registrationQuality", RequiredString(properties, fields.RegistrationQuality));
                writer.WriteString("gridType", RequiredString(properties, fields.GridType));
                if (!properties.TryGetProperty(fields.Resolution, out var resolution) ||
                    resolution.ValueKind != JsonValueKind.Number || !resolution.TryGetDouble(out var number))
                    throw new FormatException("Overlay resolution must be a number.");
                writer.WriteNumber("bestResolution", number);
                writer.WriteString("photoTime", photoTime);
                writer.WritePropertyName("roiFootprint");
                GeometryUtilities.WriteGeoJson(writer, geometry);
                CopyOptional(writer, properties, fields.ImageUrl, "imageUrl");
                CopyOptional(writer, properties, fields.SensorType, "sensorType");
                CopyOptional(writer, properties, fields.GridUri, "gridURI");
                CopyDimension(writer, properties, fields.Width, "width");
                CopyDimension(writer, properties, fields.Height, "height");
                CopyOptional(writer, properties, fields.ModeSar, "modeSAR");
                CopyOptional(writer, properties, fields.AreaOfInterest, "areaOfInterest");
                CopyOptional(writer, properties, fields.LegId, "legId");
                CopyOptional(writer, properties, fields.PrevOverlayId, "prevOverlayId");
                CopyOptional(writer, properties, fields.NextOverlayId, "nextOverlayId");
                if (sortie is not null) writer.WriteString("sortieId", sortie);
                if (created is not null) writer.WriteString("createdTime", created.Value);
                if (realtime is not null) writer.WriteBoolean("isRealtime", realtime.Value);
                writer.WriteEndObject();
            }
            var payload = buffer.WrittenSpan.ToArray();
            var input = GatewayInputMessageParser.Parse(payload);
            return new(new(payload, input, created, realtime, sortie), null);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException or
                                   InvalidInputMessageException or GeometryValidationException or ElasticProviderException or ParseException)
        {
            return new(null, $"Invalid provider overlay: {ex.Message}");
        }
    }

    private Uri TableUri()
    {
        if (!Uri.TryCreate(_options.BaseUrl, UriKind.Absolute, out var baseUri) ||
            baseUri.Scheme is not ("http" or "https") || baseUri.UserInfo.Length != 0 ||
            !Uri.TryCreate(baseUri, _options.TableEntitiesPath, out var tableUri) ||
            !SameOrigin(baseUri, tableUri) || tableUri.Query.Length != 0 || tableUri.Fragment.Length != 0)
            throw new ElasticProviderException("Configure a valid provider HTTP(S) base URL and table-entities path on the same origin.");
        return tableUri;
    }

    private Uri ContinuationUri(string value)
    {
        var initial = TableUri();
        if (!Uri.TryCreate(initial, value, out var next) || !SameOrigin(initial, next) ||
            next.UserInfo.Length != 0 || next.Fragment.Length != 0)
            throw new ElasticProviderException("Provider next_page must remain on the configured origin.");
        return WithPageLimit(next);
    }

    private Uri WithPageLimit(Uri uri)
    {
        if (_options.To is < 0 or > 10_000)
            throw new ElasticProviderException("Provider To must be between 0 and 10000.");

        // Keep provider cursor/query values in their original encoded form. Only replace
        // the page-size parameter, including duplicate or differently cased copies.
        var parts = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Where(part => !string.Equals(Uri.UnescapeDataString(part.Split('=', 2)[0]),
                "to", StringComparison.OrdinalIgnoreCase));
        var query = string.Join("&", parts.Append("to=" + _options.To.ToString(CultureInfo.InvariantCulture)));
        return new UriBuilder(uri) { Query = query }.Uri;
    }

    private static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.Port == right.Port &&
        string.Equals(left.Host, right.Host, StringComparison.OrdinalIgnoreCase);

    private static TimeZoneInfo SourceZone()
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(OffsetlessResponseTimeZone); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ElasticProviderException("The provider response timezone Asia/Jerusalem is unavailable.", ex); }
    }

    private static void RequireObject(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ElasticProviderException($"{name} must be an object.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!names.Add(property.Name)) throw new ElasticProviderException($"{name} contains duplicate fields.");
    }

    private static string RequiredString(JsonElement source, string name) =>
        OptionalString(source, name) is { } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw new FormatException($"Overlay {name} is required.");

    private static string? OptionalString(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"Overlay {name} must be a string.");
        return value.GetString();
    }

    private static bool? OptionalBoolean(JsonElement source, string name)
    {
        if (!source.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"Overlay {name} must be a boolean.");
        return value.GetBoolean();
    }

    private static void CopyDimension(Utf8JsonWriter writer, JsonElement source, string? from, string to)
    {
        if (string.IsNullOrWhiteSpace(from) || !source.TryGetProperty(from, out var value)) return;
        writer.WritePropertyName(to);
        // Provider dimensions may be 40 or 40.0. Parse the exact numeric token without
        // floating-point rounding, then emit the integer required by the gateway contract.
        if (value.ValueKind == JsonValueKind.Number &&
            (value.TryGetInt32(out var dimension) ||
             int.TryParse(value.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture, out dimension)))
            writer.WriteNumberValue(dimension);
        else
            // Preserve malformed/optional dimensions for the selected pipeline to validate.
            value.WriteTo(writer);
    }

    private static void CopyOptional(Utf8JsonWriter writer, JsonElement source, string? from, string to)
    {
        if (!string.IsNullOrWhiteSpace(from) && source.TryGetProperty(from, out var value))
        {
            // Preserve optional values, including type errors, for the selected pipeline's own validation.
            writer.WritePropertyName(to);
            value.WriteTo(writer);
        }
    }
}
