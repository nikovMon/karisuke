using ImagingPipeline.RabbitMqClient;
using ImagingPipeline.Observability;
using System.Diagnostics.Metrics;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns;

/// <summary>
/// Process-wide admission for transient preparation memory and broker publications.
/// Reservations are conservative estimates, not allocated buffers or a hard process memory cap.
/// </summary>
public sealed class AutoRunExecutionResources
{
    private static readonly UpDownCounter<long> ReservationsMetric = TelemetryMeters.RulesApi.CreateUpDownCounter<long>("auto_run.preparation.reserved.bytes", "By");
    private readonly object _gate = new();
    private readonly AutoRunOptions _options;
    private readonly IAutoRunMemoryGuard? _memory;
    private readonly LinkedList<PublicationWaiter> _publicationWaiters = new();
    private long _reservedPreparationBytes;
    private int _activePublicationPermits;

    public AutoRunExecutionResources(IOptions<AutoRunOptions> options,
        IOptions<ElasticProviderOptions> providerOptions, IOptions<RabbitMqClientOptions> rabbitOptions,
        IAutoRunMemoryGuard? memory = null)
    {
        _options = options.Value;
        _memory = memory;
        // A retained page can coexist with a raw-overlay GET. Each response can need
        // approximately three times its wire size for buffering and JSON parsing.
        PreparationReservationBytes = checked(6L * providerOptions.Value.MaxResponseBytes);
        PublisherConcurrency = Math.Min(_options.PublisherConcurrency, rabbitOptions.Value.PublisherChannelPoolSize);
        if (PreparationReservationBytes <= 0 || PublisherConcurrency <= 0)
            throw new ArgumentException("Auto Run response and publisher capacity must be positive.");
    }

    public long PreparationReservationBytes { get; }
    public int PublisherConcurrency { get; }
    public long ReservedPreparationBytes { get { lock (_gate) return _reservedPreparationBytes; } }
    public int ActivePublicationPermits { get { lock (_gate) return _activePublicationPermits; } }
    public int WaitingPublicationPermits { get { lock (_gate) return _publicationWaiters.Count; } }

    public void ValidateConfiguration()
    {
        if (!_options.Enabled || _memory is null) return;
        var limit = _memory.Snapshot().LimitBytes;
        // Divide first so even an invalid, very large configured concurrency cannot overflow.
        if (limit <= 0 || _options.MaxConcurrentPreparations > limit / PreparationReservationBytes)
            throw new OptionsValidationException(Options.DefaultName, typeof(AutoRunOptions),
            ["AutoRun.MaxConcurrentPreparations multiplied by six times AutoRun.Provider.MaxResponseBytes exceeds the effective process memory limit. Lower preparation concurrency or the response limit."]);
    }

    public bool TryReservePreparation(out IDisposable? lease)
    {
        lock (_gate)
        {
            lease = null;
            if (_memory is not null)
            {
                var snapshot = _memory.Snapshot();
                if (snapshot.IsUnderPressure || _reservedPreparationBytes >= snapshot.HeadroomBytes ||
                    PreparationReservationBytes >= snapshot.HeadroomBytes - _reservedPreparationBytes)
                    return false;
            }
            if (PreparationReservationBytes > long.MaxValue - _reservedPreparationBytes) return false;
            _reservedPreparationBytes += PreparationReservationBytes;
            ReservationsMetric.Add(PreparationReservationBytes);
            lease = new Lease(this, preparation: true);
            return true;
        }
    }

    public ValueTask<IDisposable> AcquirePublicationAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_activePublicationPermits < PublisherConcurrency && _publicationWaiters.Count == 0)
            {
                _activePublicationPermits++;
                return ValueTask.FromResult<IDisposable>(new Lease(this, preparation: false));
            }
            var waiter = new PublicationWaiter(cancellationToken);
            waiter.Node = _publicationWaiters.AddLast(waiter);
            waiter.Registration = cancellationToken.Register(() => Cancel(waiter));
            return new ValueTask<IDisposable>(WaitAsync(waiter));
        }
    }

    private static async Task<IDisposable> WaitAsync(PublicationWaiter waiter)
    {
        try { return await waiter.Completion.Task.ConfigureAwait(false); }
        finally { await waiter.Registration.DisposeAsync().ConfigureAwait(false); }
    }

    private void Cancel(PublicationWaiter waiter)
    {
        lock (_gate)
        {
            if (waiter.Node is null) return;
            _publicationWaiters.Remove(waiter.Node);
            waiter.Node = null;
            waiter.Completion.TrySetCanceled(waiter.CancellationToken);
        }
    }

    private void Release(bool preparation)
    {
        lock (_gate)
        {
            if (preparation)
            {
                _reservedPreparationBytes -= PreparationReservationBytes;
                ReservationsMetric.Add(-PreparationReservationBytes);
                return;
            }
            while (_publicationWaiters.First is { } first)
            {
                _publicationWaiters.RemoveFirst();
                first.Value.Node = null;
                if (first.Value.CancellationToken.IsCancellationRequested)
                {
                    first.Value.Completion.TrySetCanceled(first.Value.CancellationToken);
                    continue;
                }
                first.Value.Completion.SetResult(new Lease(this, preparation: false));
                return;
            }
            _activePublicationPermits--;
        }
    }

    private sealed class PublicationWaiter(CancellationToken cancellationToken)
    {
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource<IDisposable> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public LinkedListNode<PublicationWaiter>? Node { get; set; }
        public CancellationTokenRegistration Registration { get; set; }
    }

    private sealed class Lease(AutoRunExecutionResources owner, bool preparation) : IDisposable
    {
        private AutoRunExecutionResources? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release(preparation);
    }
}
