using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.RabbitMqClient;

internal interface IRabbitMqFlowControl
{
    Task WaitAsync(CancellationToken cancellationToken);
}

internal sealed class RabbitMqFlowControl : IRabbitMqFlowControl, IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly RabbitMqFlowControlOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger<RabbitMqFlowControl> _logger;
    private readonly object _gateLock = new();
    private TaskCompletionSource _gate;
    private volatile bool _throttled;
    private CancellationTokenSource? _pollCancellation;
    private Task? _pollTask;
    private bool _pollFailing;

    public RabbitMqFlowControl(
        IOptions<RabbitMqFlowControlOptions> options,
        ILogger<RabbitMqFlowControl> logger)
        : this(options, httpClient: null, logger)
    {
    }

    internal RabbitMqFlowControl(
        IOptions<RabbitMqFlowControlOptions> options,
        HttpClient? httpClient,
        ILogger<RabbitMqFlowControl> logger)
    {
        _options = options.Value;
        _logger = logger;
        _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.SetResult();

        if (httpClient is not null)
        {
            _httpClient = httpClient;
        }
        else
        {
            var handler = new HttpClientHandler();
            _httpClient = new HttpClient(handler, disposeHandler: true)
            {
                BaseAddress = _options.ManagementUri,
                Timeout = TimeSpan.FromSeconds(10)
            };

            var credentials = Convert.ToBase64String(
                System.Text.Encoding.ASCII.GetBytes($"{_options.Username}:{_options.Password}"));
            _httpClient.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", credentials);
        }
    }

    public Task WaitAsync(CancellationToken cancellationToken)
    {
        if (!_throttled)
        {
            return Task.CompletedTask;
        }

        lock (_gateLock)
        {
            if (!_throttled)
            {
                return Task.CompletedTask;
            }

            return _options.MaxWaitSeconds > 0
                ? _gate.Task.WaitAsync(TimeSpan.FromSeconds(_options.MaxWaitSeconds), cancellationToken)
                : _gate.Task.WaitAsync(cancellationToken);
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_options.WatchedQueues.Count == 0)
        {
            return Task.CompletedTask;
        }

        _pollCancellation = new CancellationTokenSource();
        _pollTask = PollLoopAsync(_pollCancellation.Token);
        RabbitMqLog.FlowControlStarted(
            _logger,
            _options.WatchedQueues.Count,
            _options.PollIntervalSeconds);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pollCancellation is null)
        {
            return;
        }

        await _pollCancellation.CancelAsync();
        if (_pollTask is not null)
        {
            try
            {
                await _pollTask.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The poll loop is cancelled during StopAsync — this is the normal shutdown path.
            }
        }

        OpenGate();
    }

    private async Task PollLoopAsync(CancellationToken cancellationToken)
    {
        var vhost = Uri.EscapeDataString(_options.VirtualHost);
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var shouldThrottle = await CheckQueuesAsync(vhost, cancellationToken);
                UpdateThrottleState(shouldThrottle);
                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                if (!_pollFailing)
                {
                    _pollFailing = true;
                    RabbitMqLog.FlowControlPollFailed(_logger, ex);
                }

                await Task.Delay(TimeSpan.FromSeconds(_options.PollIntervalSeconds), cancellationToken);
            }
        }
    }

    private async Task<bool> CheckQueuesAsync(string vhost, CancellationToken cancellationToken)
    {
        foreach (var watched in _options.WatchedQueues)
        {
            var queue = Uri.EscapeDataString(watched.Queue);
            using var response = await _httpClient.GetAsync(
                $"/api/queues/{vhost}/{queue}",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                if (!_pollFailing)
                {
                    RabbitMqLog.FlowControlQueueCheckFailed(
                        _logger,
                        watched.Queue,
                        (int)response.StatusCode);
                }

                continue;
            }

            var info = await response.Content.ReadFromJsonAsync<QueueInfo>(JsonOptions, cancellationToken);
            if (info is null)
            {
                continue;
            }

            var breached = _throttled
                ? info.Messages > watched.LowWatermark
                : info.Messages >= watched.HighWatermark;

            if (breached)
            {
                if (!_throttled)
                {
                    RabbitMqLog.FlowControlThrottling(
                        _logger,
                        watched.Queue,
                        info.Messages,
                        watched.HighWatermark);
                }

                _pollFailing = false;
                return true;
            }
        }

        _pollFailing = false;
        return false;
    }

    private void UpdateThrottleState(bool shouldThrottle)
    {
        lock (_gateLock)
        {
            if (shouldThrottle && !_throttled)
            {
                _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _throttled = true;
            }
            else if (!shouldThrottle && _throttled)
            {
                OpenGateCore();
            }
        }
    }

    private void OpenGate()
    {
        lock (_gateLock)
        {
            if (_throttled)
            {
                OpenGateCore();
            }
        }
    }

    private void OpenGateCore()
    {
        _throttled = false;
        _gate.TrySetResult();
        RabbitMqLog.FlowControlResumed(_logger);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pollCancellation is not null)
        {
            await _pollCancellation.CancelAsync();
            _pollCancellation.Dispose();
        }

        _httpClient.Dispose();
        OpenGate();
    }

    private sealed class QueueInfo
    {
        public long Messages { get; set; }
    }
}
