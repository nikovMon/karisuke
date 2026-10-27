namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed partial class AutoRunService
{
    private readonly LinkedList<Job> _preparations = new();
    private readonly LinkedList<Job> _publications = new();
    private readonly Dictionary<string, Task> _running = new(StringComparer.Ordinal);
    private CancellationToken _shutdown;
    private bool _schedulerStarted;
    private bool _stopping;
    private int _activePreparations;
    private int _activePublications;
    private int _reportedQueuedPreparations;
    private int _reportedQueuedPublications;

    private int QueuedCount => _preparations.Count + _publications.Count;
    private static bool IsQueued(Job job) => job.State is AutoRunState.QueuedPreparation or AutoRunState.QueuedPublication;
    private static bool IsActiveOrQueued(Job job) => IsQueued(job) || job.State is AutoRunState.Preparing or AutoRunState.Publishing;

    private static AutoRunRequestException QueueFull() => new(409,
        "The Auto Run waiting queue is full. Wait for a queued job to start, or cancel an unused queued run and try again.")
        { Code = "auto_run_queue_full" };

    private void EnsureAccepting()
    {
        if (_stopping || _shutdown.IsCancellationRequested)
            throw new AutoRunRequestException(503, "Rules API is stopping and cannot accept Auto Run work.");
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _stopping = true;
            CancelWaitingJobs();
        }
        return base.StopAsync(cancellationToken);
    }

    private void CancelWaitingJobs()
    {
        foreach (var job in _preparations.Concat(_publications)) Finish(job, AutoRunState.Cancelled);
        _preparations.Clear();
        _publications.Clear();
        ReportQueueMetrics();
    }

    private async Task ScheduleAsync(CancellationToken stoppingToken)
    {
        lock (_gate)
        {
            _shutdown = stoppingToken;
            _schedulerStarted = true;
            DispatchAvailable();
        }
        try { await Task.Delay(Timeout.InfiniteTimeSpan, stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
        finally
        {
            Task[] running;
            lock (_gate)
            {
                _stopping = true;
                CancelWaitingJobs();
                running = _running.Values.ToArray();
            }
            // Cancellation is linked into each worker. Do not abandon tasks using retained payloads.
            await Task.WhenAll(running);
        }
    }

    // All admission, phase changes and queue removal occur while holding _gate.
    private void DispatchAvailable()
    {
        try { DispatchAvailableCore(); }
        finally { ReportQueueMetrics(); }
    }

    private void ReportQueueMetrics()
    {
        QueuedJobsMetric.Add(_preparations.Count - _reportedQueuedPreparations,
            new KeyValuePair<string, object?>("phase", "preparation"));
        QueuedJobsMetric.Add(_publications.Count - _reportedQueuedPublications,
            new KeyValuePair<string, object?>("phase", "publication"));
        _reportedQueuedPreparations = _preparations.Count;
        _reportedQueuedPublications = _publications.Count;
    }

    private void DispatchAvailableCore()
    {
        if (!_schedulerStarted || _stopping || _shutdown.IsCancellationRequested) return;
        foreach (var job in _preparations) job.WaitReason = "preparationCapacity";
        foreach (var job in _publications) job.WaitReason = "publicationCapacity";
        while (_activePreparations < _options.MaxConcurrentPreparations && _preparations.First is { } preparation)
        {
            if (!_resources.TryReservePreparation(out var reservation))
            {
                foreach (var waiting in _preparations) waiting.WaitReason = "memory";
                break;
            }
            _preparations.RemoveFirst();
            Launch(preparation.Value, preparation: true, reservation);
        }
        while (_activePublications < _options.MaxConcurrentPublications && _publications.First is { } publication)
        {
            // Ready is visible before the preparation task unwinds. Never overlap phases of one job.
            if (publication.Value.OutstandingWork != 0) break;
            _publications.RemoveFirst();
            Launch(publication.Value, preparation: false, null);
        }
    }

    private void Launch(Job job, bool preparation, IDisposable? reservation)
    {
        job.OutstandingWork++;
        job.WaitReason = null;
        job.State = preparation ? AutoRunState.Preparing : AutoRunState.Publishing;
        if (preparation)
        {
            _activePreparations++;
            job.PreparationStartedAt = time.GetUtcNow();
        }
        else
        {
            _activePublications++;
            job.StartedAt = time.GetUtcNow();
            job.ActivePublication = job.Publication;
        }
        var queuedAt = preparation ? job.CreatedAt : job.StartRequestedAt!.Value;
        ActiveJobsMetric.Add(1, new KeyValuePair<string, object?>("phase", preparation ? "preparation" : "publication"));
        QueueWaitMetric.Record(Math.Max(0, (time.GetUtcNow() - queuedAt).TotalSeconds),
            new KeyValuePair<string, object?>("phase", preparation ? "preparation" : "publication"));
        logger.LogInformation("Auto-run {AutoRunId} {Phase} started after {QueueWaitMs} ms; active preparations {Preparations}, active publications {Publications}, queued jobs {QueuedJobs}.",
            job.Id, preparation ? "preparation" : "publication", (time.GetUtcNow() - queuedAt).TotalMilliseconds,
            _activePreparations, _activePublications, QueuedCount);
        _running.Add(job.Id, Task.Run(async () =>
        {
            try { await ProcessJobAsync(job, preparation, _shutdown); }
            finally
            {
                reservation?.Dispose();
                lock (_gate)
                {
                    if (preparation) _activePreparations--; else _activePublications--;
                    ActiveJobsMetric.Add(-1, new KeyValuePair<string, object?>("phase", preparation ? "preparation" : "publication"));
                    job.OutstandingWork--;
                    _running.Remove(job.Id);
                    if (job.DeletionRequested) Remove(job);
                    DispatchAvailable();
                }
            }
        }));
    }

    private int? QueuePosition(Job job)
    {
        if (!IsQueued(job)) return null;
        var position = 1;
        foreach (var entry in job.State == AutoRunState.QueuedPreparation ? _preparations : _publications)
        {
            if (ReferenceEquals(entry, job)) return position;
            position++;
        }
        return null;
    }

    public AutoRunExecutionSnapshot Execution()
    {
        lock (_gate) return new(_options.MaxConcurrentPreparations, _options.MaxConcurrentPublications,
            _options.MaxQueuedJobs, _activePreparations, _activePublications, _preparations.Count, _publications.Count,
            _resources.PublisherConcurrency, _resources.ActivePublicationPermits, _resources.WaitingPublicationPermits,
            _resources.PreparationReservationBytes, _resources.ReservedPreparationBytes, _retainedBytes);
    }

    public IReadOnlyList<AutoRunListEntry> ListSummaries()
    {
        lock (_gate)
        {
            Sweep();
            return _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(job => new AutoRunListEntry(
                job.Id, job.State, job.Request.Source.Mode, job.IsDirect, job.CreatedAt, job.ReadyAt,
                job.PreparationStartedAt, job.StartRequestedAt, job.StartedAt, job.FinishedAt,
                job.State == AutoRunState.Ready ? job.ReadyAt!.Value.AddMinutes(_options.ReadyLifetimeMinutes) :
                    job.FinishedAt?.AddMinutes(_options.SummaryLifetimeMinutes),
                new(job.Scanned, job.Invalid, job.Duplicates, job.ObservedMatches, job.Items.Count,
                    job.Confirmed, job.Failed, job.Uncertain, job.InFlight,
                    job.Items.Count - job.Confirmed - job.Failed - job.Uncertain - job.InFlight),
                job.RuleBytes + job.PayloadBytes + job.DiagnosticBytes, job.DeletionRequested,
                QueuePosition(job), job.WaitReason, time.GetUtcNow())).ToArray();
        }
    }
}
