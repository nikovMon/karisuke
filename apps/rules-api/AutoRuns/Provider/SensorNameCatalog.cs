using System.Collections.Frozen;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public interface ISensorNameCatalog
{
    Task<IReadOnlyDictionary<string, string>?> GetSnapshotAsync(CancellationToken cancellationToken);
}

public sealed class SensorNameCatalog : BackgroundService, ISensorNameCatalog
{
    public const string HttpClientName = "SensorNameCatalog";
    private readonly IHttpClientFactory _clients;
    private readonly SensorCatalogOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SensorNameCatalog> _logger;
    private readonly Uri? _uri;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly CancellationToken _shutdownToken;
    private readonly object _gate = new();
    private readonly object _lifetimeGate = new();
    private IReadOnlyDictionary<string, string>? _snapshot;
    private Task<IReadOnlyDictionary<string, string>>? _refresh;
    private bool _disposed;

    public SensorNameCatalog(IHttpClientFactory clients, IOptions<SensorCatalogOptions> configured,
        IOptions<AutoRunOptions> autoRun, TimeProvider time, ILogger<SensorNameCatalog> logger)
    {
        _clients = clients;
        _options = configured.Value;
        _time = time;
        _logger = logger;
        _shutdownToken = _shutdown.Token;
        if (autoRun.Value.Enabled && !string.IsNullOrWhiteSpace(_options.BaseUrl))
            _uri = new Uri(new Uri(_options.BaseUrl), _options.Path);
    }

    public async Task<IReadOnlyDictionary<string, string>?> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_uri is null) return null;
        Task<IReadOnlyDictionary<string, string>> refresh;
        lock (_gate)
        {
            if (Volatile.Read(ref _snapshot) is { } snapshot) return snapshot;
            refresh = RefreshAsync();
        }
        // A cancelled preparation stops waiting without cancelling the shared initial load.
        return await refresh.WaitAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_uri is null) return;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try { await RefreshAsync(); }
                catch (ElasticProviderException) { /* Keep retrying after an initial outage. */ }
                await Task.Delay(TimeSpan.FromMinutes(_options.RefreshIntervalMinutes), _time, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested || _shutdownToken.IsCancellationRequested) { }
    }

    private Task<IReadOnlyDictionary<string, string>> RefreshAsync()
    {
        lock (_gate)
        {
            if (_refresh is null || _refresh.IsCompleted) _refresh = LoadAsync();
            return _refresh;
        }
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(_options.TimeoutSeconds), _time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken, timeout.Token);
            using var client = _clients.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Get, _uri);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException(null, null, response.StatusCode);
            if (response.Content.Headers.ContentLength > SensorCatalogOptions.MaxResponseBytes)
                throw new InvalidDataException();
            await using var input = await response.Content.ReadAsStreamAsync(linked.Token);
            using var body = new MemoryStream();
            var buffer = new byte[8192];
            int count;
            while ((count = await input.ReadAsync(buffer, linked.Token)) != 0)
            {
                if (body.Length + count > SensorCatalogOptions.MaxResponseBytes) throw new InvalidDataException();
                body.Write(buffer, 0, count);
            }
            using var document = JsonDocument.Parse(body.GetBuffer().AsMemory(0, checked((int)body.Length)));
            var snapshot = Parse(document.RootElement);
            _shutdownToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _snapshot, snapshot);
            _logger.SensorCatalogRefreshed(snapshot.Count);
            return snapshot;
        }
        catch (Exception ex) when (!_shutdownToken.IsCancellationRequested &&
            ex is HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException)
        {
            var reason = ex switch
            {
                OperationCanceledException => "timeout",
                HttpRequestException { StatusCode: not null } => "http_error",
                HttpRequestException => "transport",
                _ => "invalid_response"
            };
            var previous = Volatile.Read(ref _snapshot);
            _logger.SensorCatalogRefreshFailed(reason, previous is not null);
            return previous ?? throw new ElasticProviderException("Sensor catalog is unavailable and has no cached names. Retry preparation after the catalog becomes available.");
        }
    }

    private static IReadOnlyDictionary<string, string> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in root.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("sensorName", out var value) ||
                value.ValueKind != JsonValueKind.String || value.GetString() is not { } name ||
                string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Any(char.IsControl))
                throw new InvalidDataException();
            if (names.TryGetValue(name, out var existing) && existing != name) throw new InvalidDataException();
            names[name] = name;
        }
        return names.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_lifetimeGate)
        {
            // Shared singleton registrations can be disposed before another host stops them.
            if (_disposed) return Task.CompletedTask;
            _shutdown.Cancel();
            return base.StopAsync(cancellationToken);
        }
    }

    public override void Dispose()
    {
        lock (_lifetimeGate)
        {
            if (_disposed) return;
            _disposed = true;
            _shutdown.Cancel();
            base.Dispose();
            _shutdown.Dispose();
        }
    }
}
