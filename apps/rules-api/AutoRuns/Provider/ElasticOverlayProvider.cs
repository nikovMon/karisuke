using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.RuleEngine.Input;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public sealed class ElasticOverlayProvider(HttpClient client, IOptions<ElasticProviderOptions> configured,
    IAutoRunMemoryGuard? memory = null)
    : IElasticOverlayProvider
{
    private const string OffsetlessResponseTimeZone = "Asia/Jerusalem";
    private readonly ElasticProviderOptions _options = configured.Value;
    private readonly IReadOnlyDictionary<string, string> _headers = ElasticProviderHeaders.Snapshot(configured.Value.Headers);

    public async Task<ProviderOverlayResult> GetAsync(string id, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var uri = WithPageLimit(new Uri(TableUri().AbsoluteUri.TrimEnd('/') + "/" + Uri.EscapeDataString(id)));
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        using var document = await SendAsync(request, "overlay lookup", allowNotFound: true, cancellationToken);
        if (document is null) return new(null, "Overlay was not found by Elastic Provider.");
        memory?.ThrowIfUnsafe();
        var result = Map(document.RootElement);
        return result.Overlay is { } overlay && !string.Equals(overlay.Input.ImageId, id, StringComparison.Ordinal)
            ? new(null, "Provider returned an overlay ID different from the requested ID.")
            : result;
    }

    public Task<ProviderPage> SearchPageAsync(JsonElement query, string? nextPage, CancellationToken cancellationToken) =>
        SearchPageCoreAsync(query, nextPage, null, cancellationToken);

    public Task<ProviderPage> VisitPageAsync(JsonElement query, string? nextPage,
        Func<ProviderOverlayResult, bool> accept, CancellationToken cancellationToken) =>
        SearchPageCoreAsync(query, nextPage, accept, cancellationToken);

    private async Task<ProviderPage> SearchPageCoreAsync(JsonElement query, string? nextPage,
        Func<ProviderOverlayResult, bool>? accept, CancellationToken cancellationToken)
    {
        if (query.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("Provider query must be an object.", nameof(query));
        var uri = nextPage is null ? WithPageLimit(TableUri()) : ContinuationUri(nextPage);
        using var request = new HttpRequestMessage(HttpMethod.Post, uri)
        {
            Content = JsonContent.Create(query, mediaType: new MediaTypeHeaderValue("application/json-patch+json"))
        };
        using var document = await SendAsync(request, nextPage is null ? "initial search page" : "continuation page",
            allowNotFound: false, cancellationToken)
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
            else if (!accept(item)) return new([], null);
        }
        return new(items, continuation);
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

    private async Task<JsonDocument?> SendAsync(HttpRequestMessage request, string operation,
        bool allowNotFound, CancellationToken cancellationToken)
    {
        memory?.ThrowIfUnsafe();
        foreach (var (name, value) in _headers)
            if (!request.Headers.TryAddWithoutValidation(name, value))
                throw new ElasticProviderException(ElasticProviderHeaders.ValidationError);
        if (_options.TimeoutSeconds <= 0)
            throw new ElasticProviderException("Provider timeout must be positive.");
        if (_options.MaxResponseBytes is <= 0 or > 536_870_912)
            throw new ElasticProviderException("Provider MaxResponseBytes must be between 1 and 536870912 bytes.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(_options.TimeoutSeconds));
        var phase = "waiting for response headers";
        long bytesRead = 0;
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            phase = "reading the response body";
            if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
                throw new ElasticProviderException($"Elastic Provider returned HTTP {(int)response.StatusCode}.");
            if (response.RequestMessage?.RequestUri is { } effective && !SameOrigin(effective, TableUri()))
                throw new ElasticProviderException("Provider response redirected outside the configured origin.");
            if (response.Content.Headers.ContentLength > _options.MaxResponseBytes)
                throw new ProviderResponseLimitException(_options.MaxResponseBytes);
            if (response.Content.Headers.ContentLength is > 0 and var length)
                memory?.ThrowIfUnsafe(length * 3);
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            using var buffer = new MemoryStream();
            var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(chunk.AsMemory(0, chunk.Length), timeout.Token)) != 0)
                {
                    bytesRead += read;
                    var required = buffer.Length + read;
                    if (required > _options.MaxResponseBytes)
                        throw new ProviderResponseLimitException(_options.MaxResponseBytes);
                    if (required > buffer.Capacity)
                    {
                        var capacity = Math.Min(_options.MaxResponseBytes,
                            Math.Max(required, Math.Max(256L, 2L * buffer.Capacity)));
                        // Growth temporarily retains both arrays. Current usage includes the old
                        // array; reserve the new one and parsing space before allocating it.
                        memory?.ThrowIfUnsafe(capacity + required * 2);
                        buffer.Capacity = checked((int)capacity);
                    }
                    buffer.Write(chunk, 0, read);
                }
                memory?.ThrowIfUnsafe(buffer.Length * 2);
                return JsonDocument.Parse(buffer.GetBuffer().AsMemory(0, checked((int)buffer.Length)));
            }
            finally { ArrayPool<byte>.Shared.Return(chunk); }
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ElasticProviderException(
                $"Elastic Provider request timed out during {operation} while {phase}. " +
                $"Configured per-request timeout: {_options.TimeoutSeconds} seconds (AutoRun.Provider.TimeoutSeconds); " +
                $"response bytes read: {bytesRead}. Narrow the search or increase that timeout and prepare again.", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new ElasticProviderException("Elastic Provider request failed.", ex);
        }
        catch (JsonException ex)
        {
            throw new ElasticProviderException("Elastic Provider returned invalid JSON.", ex);
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
