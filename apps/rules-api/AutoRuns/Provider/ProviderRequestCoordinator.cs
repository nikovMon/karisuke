using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns.Provider;

public sealed record ProviderRequestSnapshot(
    int MaxConcurrentRequests, int ActiveRequests, int WaitingRequests,
    int RequestDelayMs, DateTimeOffset? CooldownUntil);

/// <summary>One instance owns FIFO request admission, pacing and retry cooldown across all runs and clients.</summary>
public sealed class ProviderRequestCoordinator(IOptions<ElasticProviderOptions> configured, TimeProvider time)
{
    private readonly object _gate = new();
    private readonly LinkedList<object> _waiting = new();
    private readonly int _maximum = configured.Value.MaxConcurrentRequests;
    private readonly TimeSpan _pacing = TimeSpan.FromMilliseconds(configured.Value.RequestDelayMs);
    private TaskCompletionSource _changed = NewSignal();
    private int _active;
    private long? _lastStarted;
    private long? _lastFinished;
    private long? _cooldownStarted;
    private TimeSpan _cooldown;

    public ProviderRequestSnapshot Snapshot()
    {
        lock (_gate)
        {
            var remaining = CooldownRemaining();
            return new(_maximum, _active, _waiting.Count, (int)_pacing.TotalMilliseconds,
                remaining > TimeSpan.Zero ? time.GetUtcNow() + remaining : null);
        }
    }

    public async Task<IDisposable> AcquireAsync(string operation, int attempt, int maxAttempts,
        int? lastStatus, Action<ProviderProgress>? report, CancellationToken cancellationToken)
    {
        if (_maximum <= 0) throw new InvalidOperationException("Provider MaxConcurrentRequests must be positive.");
        cancellationToken.ThrowIfCancellationRequested();
        LinkedListNode<object> node;
        lock (_gate) { node = _waiting.AddLast(new object()); Signal(); }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Task changed;
                TimeSpan delay;
                ProviderProgress progress;
                lock (_gate)
                {
                    changed = _changed.Task;
                    if (_waiting.First != node || _active >= _maximum)
                    {
                        delay = Timeout.InfiniteTimeSpan;
                        progress = new("capacityWait", operation, attempt, maxAttempts, lastStatus);
                    }
                    else
                    {
                        var cooldown = CooldownRemaining();
                        delay = Max(cooldown, Max(PacingRemaining(_lastStarted), PacingRemaining(_lastFinished)));
                        if (delay <= TimeSpan.Zero)
                        {
                            _waiting.Remove(node);
                            _active++;
                            _lastStarted = time.GetTimestamp();
                            Signal();
                            return new Lease(this);
                        }
                        progress = new(cooldown > TimeSpan.Zero ? "retryWait" : "requestDelay",
                            operation, attempt, maxAttempts, lastStatus, time.GetUtcNow() + delay);
                    }
                }
                // Never invoke the job's callback while holding the shared coordinator lock.
                report?.Invoke(progress);
                if (delay == Timeout.InfiniteTimeSpan) await changed.WaitAsync(cancellationToken);
                else
                {
                    try { await changed.WaitAsync(delay, time, cancellationToken); }
                    catch (TimeoutException) { /* Recheck capacity and deadlines atomically. */ }
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                if (node.List is not null) { _waiting.Remove(node); Signal(); }
            }
        }
    }

    public void ExtendCooldown(TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero) return;
        lock (_gate)
        {
            if (delay <= CooldownRemaining()) return;
            _cooldownStarted = time.GetTimestamp();
            _cooldown = delay;
            Signal();
        }
    }

    private TimeSpan PacingRemaining(long? timestamp) => timestamp is { } value
        ? _pacing - time.GetElapsedTime(value) : TimeSpan.Zero;

    private TimeSpan CooldownRemaining() => _cooldownStarted is { } value
        ? _cooldown - time.GetElapsedTime(value) : TimeSpan.Zero;

    private void Release()
    {
        lock (_gate)
        {
            _active--;
            _lastFinished = time.GetTimestamp();
            Signal();
        }
    }

    private static TimeSpan Max(TimeSpan first, TimeSpan second) => first > second ? first : second;
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void Signal() { var prior = _changed; _changed = NewSignal(); prior.TrySetResult(); }

    private sealed class Lease(ProviderRequestCoordinator owner) : IDisposable
    {
        private ProviderRequestCoordinator? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
    }
}
