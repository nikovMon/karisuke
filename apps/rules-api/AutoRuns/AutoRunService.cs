using System.ComponentModel.DataAnnotations;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;
using System.Threading.Channels;
using ImagingPipeline.GeometryUtils;
using ImagingPipeline.Observability;
using ImagingPipeline.PipelineContracts;
using ImagingPipeline.RuleEngine.Rules;
using ImagingPipeline.Rules.Api.AutoRuns.Provider;
using Microsoft.Extensions.Options;

namespace ImagingPipeline.Rules.Api.AutoRuns;

/// <summary>
/// One in-process coordinator owns admission, snapshots, payload accounting and job state.
/// HTTP cancellation only cancels preview admission; accepted work belongs to its job.
/// </summary>
public sealed class AutoRunService(
    IAutoRunRuleResolver resolver,
    IElasticOverlayProvider provider,
    ProviderQueryBuilder queryBuilder,
    IAutoRunPublisher publisher,
    IOptions<AutoRunOptions> configured,
    TimeProvider time,
    ILogger<AutoRunService> logger) : BackgroundService
{
    private static readonly Counter<long> SelectedMetric = TelemetryMeters.RulesApi.CreateCounter<long>("auto_run.selected.overlays");
    private static readonly Counter<long> PublicationsMetric = TelemetryMeters.RulesApi.CreateCounter<long>("auto_run.publications");
    private readonly AutoRunOptions _options = configured.Value;
    private readonly object _gate = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly Channel<Job> _work = Channel.CreateUnbounded<Job>(new() { SingleReader = true });
    private string? _active;
    private long _retainedBytes;

    public async Task<AutoRunSummary> PreviewAsync(AutoRunPreviewRequest request, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new AutoRunRequestException(503, "Historical auto-run is disabled.");
        if (request is null) throw new AutoRunRequestException(400, "An auto-run request is required.");
        request = request with { Publication = request.Publication ?? _options.Publication with { } };
        Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) EnsureAvailable();
        IReadOnlyList<AutoRunResolvedTarget> targets = request.Targets.Count == 0
            ? [] : await resolver.ResolveAsync(request.Targets, cancellationToken);
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request, AutoRunJson.Options);
        var frozen = JsonSerializer.Deserialize<AutoRunPreviewRequest>(requestBytes, AutoRunJson.Options)!;
        var rulesBytes = requestBytes.LongLength +
            targets.Sum(t => (long)JsonSerializer.SerializeToUtf8Bytes(t.Rule, AutoRunJson.Options).Length);
        var job = new Job(frozen, targets, time.GetUtcNow(), Maximum(frozen));
        if (job.IsDirect)
            AddWarning(job, "These overlays will be submitted as ordinary gateway input. Current active gateway rules, including photo-age limits, apply; pipeline assignments and downstream processing are not predicted by this preview.");
        lock (_gate)
        {
            EnsureAvailable();
            if (_jobs.Count >= _options.MaxRetainedJobs)
                throw new AutoRunRequestException(409, "The retained-job limit is reached. Wait for previews/summaries to expire.");
            if (rulesBytes > _options.PayloadBudgetBytes - _retainedBytes)
                throw new AutoRunRequestException(409, "The retained payload budget is full. Cancel an unused preview or wait for expiry.");
            job.RuleBytes = rulesBytes;
            _retainedBytes += rulesBytes;
            _jobs.Add(job.Id, job);
            _active = job.Id;
            _work.Writer.TryWrite(job);
            return Summary(job);
        }
    }

    public Task<AutoRunSummary> RerunAsync(string id, CancellationToken cancellationToken)
    {
        AutoRunPreviewRequest request;
        lock (_gate)
        {
            Sweep();
            var job = Find(id);
            if (job.State is AutoRunState.Preparing or AutoRunState.Publishing)
                throw new AutoRunRequestException(409, "Wait for the active run to finish or cancel it before preparing a rerun.");
            request = job.Request with { Publication = job.Publication with { } };
        }
        // Resolve current saved rules and fetch fresh overlays, leaving the original snapshot intact.
        return PreviewAsync(request, cancellationToken);
    }

    public AutoRunSummary Get(string id)
    {
        lock (_gate) { Sweep(); return Summary(Find(id)); }
    }

    public IReadOnlyList<AutoRunSummary> List()
    {
        lock (_gate) { Sweep(); return _jobs.Values.OrderByDescending(j => j.CreatedAt).Select(Summary).ToArray(); }
    }

    public AutoRunItemsPage Items(string id, int from, int size, AutoRunItemOutcome? outcome)
    {
        if (from < 0 || size is < 1 or > 200 || outcome is { } value && !Enum.IsDefined(value))
            throw new AutoRunRequestException(400, "Use from >= 0, size 1–200, and a valid outcome.");
        lock (_gate)
        {
            Sweep();
            var job = Find(id);
            var items = job.Items.Where(i => outcome is null || i.Outcome == outcome).ToArray();
            return new(from, size, items.Length, items.Skip(from).Take(size).Select(item => new AutoRunItem(
                item.Id, item.SensorName, item.PhotoTime,
                item.Assignments.Select(i => new AutoRunItemTarget(job.Targets[i].Target.PipelineId, job.Targets[i].Target.RuleId)).ToArray(),
                item.Outcome, item.Error)).ToArray());
        }
    }

    public AutoRunSummary Start(string id)
    {
        lock (_gate)
        {
            Sweep();
            var job = Find(id);
            if (job.StartedAt is not null) return Summary(job);
            if (job.State != AutoRunState.Ready)
                throw new AutoRunRequestException(409, "Only a ready, unexpired preview can be started.");
            EnsureAvailable();
            job.State = AutoRunState.Publishing;
            job.StartedAt = time.GetUtcNow();
            job.ActivePublication = job.Publication;
            _active = id;
            _work.Writer.TryWrite(job);
            return Summary(job);
        }
    }

    public AutoRunSummary UpdatePublication(string id, AutoRunPublication settings)
    {
        ValidatePublication(settings);
        lock (_gate)
        {
            Sweep();
            var job = Find(id);
            if (job.State is not (AutoRunState.Preparing or AutoRunState.Ready or AutoRunState.Publishing))
                throw new AutoRunRequestException(409, "Publication settings cannot change for a terminal job.");
            job.Publication = settings with { };
            if (job.State != AutoRunState.Publishing) job.ActivePublication = job.Publication;
            return Summary(job);
        }
    }

    public AutoRunSummary Cancel(string id)
    {
        lock (_gate)
        {
            Sweep();
            var job = Find(id);
            if (job.State is AutoRunState.Preparing or AutoRunState.Publishing)
            {
                job.Cancellation.Cancel();
                // The worker releases ownership after observing all in-flight outcomes.
                AddWarning(job, "Cancellation requested. Already submitted messages cannot be recalled.");
            }
            else if (job.State == AutoRunState.Ready) Finish(job, AutoRunState.Cancelled);
            return Summary(job);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) =>
        await Task.WhenAll(ProcessAsync(stoppingToken), SweepAsync(stoppingToken));

    private async Task ProcessAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in _work.Reader.ReadAllAsync(stoppingToken))
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
                using var scope = logger.BeginScope(new Dictionary<string, object?> { ["auto_run.id"] = job.Id });
                using var activity = TelemetrySources.RulesApi.StartActivity(
                    job.State == AutoRunState.Preparing ? "auto_run.prepare" : "auto_run.publish",
                    ActivityKind.Internal, job.TraceContext);
                activity?.SetTag("auto_run.id", job.Id);
                try
                {
                    linked.Token.ThrowIfCancellationRequested();
                    if (job.State == AutoRunState.Preparing) await PrepareAsync(job, linked.Token);
                    else await PublishAsync(job, linked.Token);
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    lock (_gate) Finish(job, AutoRunState.Cancelled);
                }
                catch (Exception ex)
                {
                    activity?.SetStatus(ActivityStatusCode.Error, "Auto-run failed.");
                    logger.LogError(ex, "Auto-run failed.");
                    lock (_gate)
                    {
                        AddError(job, ex is InvalidOperationException or ArgumentException or HttpRequestException or ElasticProviderException
                            ? ex.Message : "Auto-run failed. Check the service logs using the run ID.");
                        Finish(job, AutoRunState.Failed);
                    }
                }
                finally
                {
                    lock (_gate)
                    {
                        logger.LogInformation("Auto-run is {AutoRunState}; selected {Selected}, confirmed {Confirmed}, failed {Failed}, uncertain {Uncertain}.",
                            job.State, job.Items.Count, job.Confirmed, job.Failed, job.Uncertain);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task PrepareAsync(Job job, CancellationToken cancellationToken)
    {
        var candidates = new List<AutoRunCandidate>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var matcher = job.IsDirect ? null : new RuleMatcher(time);
        var matchRules = job.Targets.Select(t => t.MatchRule).ToArray();
        var queryBodies = queryBuilder.Build(job.Request.Source, job.Targets.Select(t => t.Rule).ToArray());
        lock (_gate)
        {
            var queryBytes = queryBodies.Sum(q => (long)JsonSerializer.SerializeToUtf8Bytes(q).Length);
            if (queryBytes > _options.PayloadBudgetBytes - _retainedBytes)
                throw new InvalidOperationException("The process-wide payload budget was exceeded while preparing queries.");
            _retainedBytes += queryBytes;
            job.RuleBytes += queryBytes;
            job.Queries = queryBodies;
            if (job.Targets.Any(t => t.MatchRule.ModeSar is { Count: > 0 }))
                AddWarning(job, "SAR mode is verified locally; its provider response mapping must match your deployment.");
        }

        if (job.Request.Source.Mode == AutoRunSourceMode.OverlayIds)
        {
            foreach (var id in job.Request.Source.OverlayIds!.Distinct(StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AtLimit()) break;
                Accept(await provider.GetAsync(id, cancellationToken));
            }
            lock (_gate) job.SourceExhausted = job.StoppedReason is null;
        }
        else
        {
            var searches = new Queue<Search>(queryBodies.Select(q => new Search(q)));
            while (searches.TryDequeue(out var search))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AtLimit()) break;
                var page = await provider.SearchPageAsync(search.Query, search.NextPage, cancellationToken);
                foreach (var item in page.Items)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (AtLimit()) break;
                    Accept(item);
                }
                if (job.StoppedReason is not null) break;
                if (page.Items.Count > 0 && page.NextPage is { Length: > 0 } next)
                {
                    if (!search.Visited.Add(next)) throw new InvalidOperationException("Elastic Provider returned a repeated next_page URL.");
                    search.NextPage = next;
                    searches.Enqueue(search);
                }
            }
            lock (_gate) job.SourceExhausted = job.StoppedReason is null;
        }
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AutoRunSelection> selection = job.IsDirect
            ? candidates.OrderByDescending(c => c.Overlay.Input.PhotoTime)
                .ThenBy(c => c.Overlay.Input.ImageId, StringComparer.Ordinal)
                .Take(job.Maximum).Select(c => new AutoRunSelection(c, [])).ToArray()
            : AutoRunSelector.Select(candidates, job.Targets.Select(t => t.Target).ToArray(), job.Maximum, cancellationToken);
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            job.Items = selection.Select(s => new Item(s.Candidate.Overlay, s.Assignments)).ToList();
            var keptBytes = job.Items.Sum(i => (long)i.Payload!.Length);
            _retainedBytes -= job.PayloadBytes - keptBytes;
            job.PayloadBytes = keptBytes;
            foreach (var item in job.Items)
                foreach (var index in item.Assignments) job.SelectedByTarget[index]++;
            if (job.Items.Count < job.Request.MinOverlays || job.Targets.Where((t, i) => job.SelectedByTarget[i] < t.Target.MinOverlays).Any())
                AddWarning(job, "One or more minimums are not met by this selection. Available overlays can still be submitted.");
            job.ReadyAt = time.GetUtcNow();
            job.State = AutoRunState.Ready;
            if (_active == job.Id) _active = null;
            SelectedMetric.Add(job.Items.Count);
        }

        bool AtLimit()
        {
            lock (_gate)
            {
                if (job.Scanned < _options.MaxScannedRecords && candidates.Count < _options.MaxCandidates) return false;
                job.StoppedReason = job.Scanned >= _options.MaxScannedRecords ? "scanLimit" : "candidateLimit";
                AddWarning(job, $"Preparation stopped at {job.StoppedReason}; observed matches are not the total source count.");
                return true;
            }
        }

        void Accept(ProviderOverlayResult result)
        {
            lock (_gate) job.Scanned++;
            if (result.Overlay is not { } overlay)
            {
                lock (_gate) { job.Invalid++; AddError(job, result.Error ?? "Invalid provider overlay."); }
                return;
            }
            if (!seen.Add(overlay.Input.ImageId))
            {
                lock (_gate) job.Duplicates++;
                return;
            }
            if (job.Request.Source.Filters is { } filters && !ProviderQueryBuilder.Matches(overlay, filters)) return;
            if (job.IsDirect)
            {
                Retain(overlay, []);
                return;
            }
            var evaluation = matcher!.Match(overlay.Input, matchRules);
            if (evaluation.Matches.Count == 0) return;
            using var document = JsonDocument.Parse(overlay.Payload);
            PipelineDispatchContext context;
            try
            {
                context = OverlayDispatchContextFactory.Create(overlay.Input.ImageId, overlay.Input.ImageId, document.RootElement);
            }
            catch (ArgumentException ex)
            {
                lock (_gate) { job.Invalid++; AddError(job, $"Overlay '{overlay.Input.ImageId}' has invalid dispatch metadata: {ex.Message}"); }
                return;
            }
            var matches = new List<int>();
            foreach (var match in evaluation.Matches)
            {
                // IDs may coincide across pipelines. MatchRule identity is the frozen object.
                var index = Array.FindIndex(matchRules, r => ReferenceEquals(r, match.Rule));
                var target = job.Targets[index];
                var unitContext = context with
                {
                    RuleId = target.Rule.Id,
                    RoiFootprint = GeometryUtilities.WriteGeoJson(match.IntersectionGeometry),
                    RuleLocationWkt = match.Rule.Geometry is { } geometry ? GeometryUtilities.WriteWkt(geometry) : null
                };
                var errors = target.Rule.RunParams!.SelectMany(p => target.Contract.ValidateContext(unitContext, p)).ToArray();
                if (errors.Length == 0) matches.Add(index);
                else lock (_gate) AddError(job, $"Overlay '{overlay.Input.ImageId}' cannot enter pipeline '{target.Target.PipelineId}': {string.Join("; ", errors.Select(e => e.Message))}");
            }
            if (matches.Count == 0)
            {
                lock (_gate) job.Invalid++;
                return;
            }
            Retain(overlay, matches.ToArray());
        }

        void Retain(ProviderOverlay overlay, int[] matches)
        {
            lock (_gate)
            {
                job.ObservedMatches++;
                if (overlay.Payload.LongLength > _options.PayloadBudgetBytes - _retainedBytes)
                    throw new InvalidOperationException("The process-wide payload budget was exceeded; no runnable preview was created.");
                _retainedBytes += overlay.Payload.LongLength;
                job.PayloadBytes += overlay.Payload.LongLength;
            }
            // Direct submission owns the exact normalized bytes selected by preview.
            candidates.Add(new(job.IsDirect ? overlay with { Payload = overlay.Payload.ToArray() } : overlay, matches));
        }
    }

    private async Task PublishAsync(Job job, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < job.Items.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AutoRunPublication settings;
            lock (_gate) { settings = job.Publication; job.ActivePublication = settings; }
            if (offset > 0 && settings.DelayMs > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(settings.DelayMs), time, cancellationToken);
            var batch = job.Items.Skip(offset).Take(settings.BatchSize).ToArray();
            await Parallel.ForEachAsync(batch, new ParallelOptions
            {
                MaxDegreeOfParallelism = _options.PublisherConcurrency,
                CancellationToken = cancellationToken
            }, async (item, token) =>
            {
                token.ThrowIfCancellationRequested();
                lock (_gate) { item.Outcome = AutoRunItemOutcome.InFlight; job.InFlight++; }
                AutoRunPublishResult result;
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.PublishTimeoutSeconds));
                try
                {
                    result = await publisher.PublishAsync(job.Id, item.Payload!,
                        item.Assignments.Select(i => job.Targets[i]).ToArray(), timeout.Token);
                    if (result.Outcome is not (AutoRunItemOutcome.Confirmed or AutoRunItemOutcome.Failed or AutoRunItemOutcome.Uncertain))
                        result = new(AutoRunItemOutcome.Uncertain, "Publisher returned no definitive outcome.");
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Publication outcome is uncertain for overlay {OverlayId}.", item.Id);
                    result = new(AutoRunItemOutcome.Uncertain, "Publication did not return a definitive outcome.");
                }
                lock (_gate)
                {
                    job.InFlight--;
                    item.Outcome = result.Outcome;
                    item.Error = Truncate(result.Error);
                    if (result.Outcome == AutoRunItemOutcome.Confirmed) job.Confirmed++;
                    else if (result.Outcome == AutoRunItemOutcome.Failed) job.Failed++;
                    else job.Uncertain++;
                    foreach (var index in item.Assignments)
                    {
                        if (result.Outcome == AutoRunItemOutcome.Confirmed) job.ConfirmedByTarget[index]++;
                        else if (result.Outcome == AutoRunItemOutcome.Failed) job.FailedByTarget[index]++;
                        else job.UncertainByTarget[index]++;
                    }
                    PublicationsMetric.Add(1, new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
                    if (result.Outcome != AutoRunItemOutcome.Confirmed)
                        logger.LogWarning("Overlay {OverlayId} publication is {PublicationOutcome}: {PublicationError}", item.Id, result.Outcome, result.Error);
                }
            });
            offset += batch.Length;
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate) Finish(job, job.Failed + job.Uncertain == 0 ? AutoRunState.Completed : AutoRunState.CompletedWithErrors);
    }

    private async Task SweepAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), time);
            while (await timer.WaitForNextTickAsync(cancellationToken))
                lock (_gate) Sweep();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    // Call with _gate held.
    private void Sweep()
    {
        var now = time.GetUtcNow();
        foreach (var job in _jobs.Values)
            if (job.State == AutoRunState.Ready && now >= job.ReadyAt!.Value.AddMinutes(_options.ReadyLifetimeMinutes))
                Finish(job, AutoRunState.Expired);
        foreach (var job in _jobs.Values.Where(j => j.FinishedAt is { } finished &&
                     now >= finished.AddMinutes(_options.SummaryLifetimeMinutes)).ToArray())
        {
            _retainedBytes -= job.RuleBytes;
            job.Cancellation.Dispose();
            _jobs.Remove(job.Id);
        }
    }

    private void Finish(Job job, AutoRunState state)
    {
        job.State = state;
        if (_active == job.Id) _active = null;
        job.FinishedAt = time.GetUtcNow();
        foreach (var item in job.Items) item.Payload = null;
        _retainedBytes -= job.PayloadBytes;
        job.PayloadBytes = 0;
    }

    private void EnsureAvailable()
    {
        Sweep();
        if (_active is not null)
            throw new AutoRunRequestException(409, $"Auto-run '{_active}' is preparing or publishing. Only one active job is supported.");
    }

    private Job Find(string id) => _jobs.GetValueOrDefault(id) ??
        throw new AutoRunRequestException(404, "Auto-run not found. It may have expired or been lost when this instance restarted.");

    private AutoRunSummary Summary(Job job) => new(
        job.Id, job.State, job.CreatedAt, job.ReadyAt, job.StartedAt, job.FinishedAt,
        job.State == AutoRunState.Ready ? job.ReadyAt!.Value.AddMinutes(_options.ReadyLifetimeMinutes) :
        job.FinishedAt?.AddMinutes(_options.SummaryLifetimeMinutes),
        job.Request.Source.Mode, job.Request.MinOverlays, job.Maximum,
        Math.Max(0, job.Request.MinOverlays - job.Items.Count),
        new(job.Scanned, job.Invalid, job.Duplicates, job.ObservedMatches, job.Items.Count,
            job.Confirmed, job.Failed, job.Uncertain, job.InFlight,
            job.Items.Count - job.Confirmed - job.Failed - job.Uncertain - job.InFlight),
        job.Targets.Select((t, i) => new AutoRunTargetSummary(t.Target.PipelineId, t.Target.RuleId,
            t.Rule.RuleName, t.Rule.IsActive, t.Target.MinOverlays, t.Target.MaxOverlays!.Value,
            job.SelectedByTarget[i], job.ConfirmedByTarget[i], job.FailedByTarget[i], job.UncertainByTarget[i],
            Math.Max(0, t.Target.MinOverlays - job.SelectedByTarget[i]), t.Rule)).ToArray(),
        job.Publication, job.ActivePublication, job.Publication != job.ActivePublication,
        job.SourceExhausted, job.StoppedReason, job.Warnings.ToArray(), job.Errors.ToArray(), job.Queries,
        job.StartedAt is { } start ? job.Confirmed / Math.Max(1, ((job.FinishedAt ?? time.GetUtcNow()) - start).TotalSeconds) : 0);

    private void Validate(AutoRunPreviewRequest request)
    {
        if (request.Source is null || request.Targets is not { Count: <= 100 })
            throw new AutoRunRequestException(400, "A source and at most 100 targets are required; targets cannot be null.");
        if (!Enum.IsDefined(request.Source.Mode)) throw new AutoRunRequestException(400, "Unknown source mode.");
        if (request.Source.Mode == AutoRunSourceMode.Rules && request.Targets.Count == 0)
            throw new AutoRunRequestException(400, "Rules mode requires at least one pipeline rule target.");
        if (request.Source.Mode == AutoRunSourceMode.OverlayIds)
        {
            if (request.Source.Filters is not null || request.Source.OverlayIds is not { Length: > 0 and <= 100_000 } ids ||
                ids.Any(string.IsNullOrWhiteSpace))
                throw new AutoRunRequestException(400, "OverlayIds mode requires 1–100000 nonempty IDs and cannot include filters.");
        }
        else if (request.Source.OverlayIds is not null)
            throw new AutoRunRequestException(400, "Only OverlayIds mode accepts overlayIds.");
        if (request.Source.Filters is { } filters)
        {
            var errors = ProviderQueryBuilder.Validate(filters).ToArray();
            if (errors.Length > 0) throw new AutoRunRequestException(400, string.Join("; ", errors));
        }
        var keys = new HashSet<(string, string)>();
        foreach (var target in request.Targets)
        {
            if (target is null || string.IsNullOrWhiteSpace(target.PipelineId) || string.IsNullOrWhiteSpace(target.RuleId) ||
                target.MaxOverlays is not (> 0) || target.MaxOverlays > _options.MaxSelectedOverlays ||
                target.MinOverlays < 0 || target.MinOverlays > target.MaxOverlays ||
                !keys.Add((target.PipelineId, target.RuleId)))
                throw new AutoRunRequestException(400, "Targets must be unique pipeline/rule pairs with 0 <= minOverlays <= maxOverlays within the selected-overlay limit.");
        }
        var maximum = Maximum(request);
        if (maximum <= 0 || maximum > _options.MaxSelectedOverlays || request.MinOverlays < 0 || request.MinOverlays > maximum)
            throw new AutoRunRequestException(400, "Overall quotas must satisfy 0 <= minOverlays <= maxOverlays within the selected-overlay limit.");
        ValidatePublication(request.Publication!);
    }

    private int Maximum(AutoRunPreviewRequest request) => request.MaxOverlays ?? (int)Math.Min(
        request.Targets.Count == 0
            ? request.Source.Mode == AutoRunSourceMode.OverlayIds
                ? request.Source.OverlayIds!.Distinct(StringComparer.Ordinal).LongCount()
                : AutoRunOptions.DefaultFilterMaxOverlays
            : request.Targets.Sum(t => (long)t.MaxOverlays!.Value),
        _options.MaxSelectedOverlays);

    private static void ValidatePublication(AutoRunPublication publication)
    {
        if (publication is null || !Validator.TryValidateObject(publication, new ValidationContext(publication), [], true))
            throw new AutoRunRequestException(400, "Use batchSize 1–100 and delayMs 0–60000.");
    }
    private static string? Truncate(string? text) => text?.Length > 1000 ? text[..1000] : text;
    private static void AddError(Job job, string text) { if (job.Errors.Count < 100) job.Errors.Add(Truncate(text)!); }
    private static void AddWarning(Job job, string text) { if (job.Warnings.Count < 100 && !job.Warnings.Contains(text)) job.Warnings.Add(text); }

    private sealed class Search(JsonElement query)
    {
        public JsonElement Query { get; } = query;
        public string? NextPage { get; set; }
        public HashSet<string> Visited { get; } = new(StringComparer.Ordinal);
    }
    private sealed class Item(ProviderOverlay overlay, int[] assignments)
    {
        public string Id { get; } = overlay.Input.ImageId;
        public string SensorName { get; } = overlay.Input.SensorName;
        public DateTimeOffset PhotoTime { get; } = overlay.Input.PhotoTime;
        public byte[]? Payload { get; set; } = overlay.Payload;
        public int[] Assignments { get; } = assignments;
        public AutoRunItemOutcome Outcome { get; set; }
        public string? Error { get; set; }
    }
    private sealed class Job(AutoRunPreviewRequest request, IReadOnlyList<AutoRunResolvedTarget> targets, DateTimeOffset createdAt, int maximum)
    {
        public string Id { get; } = Guid.NewGuid().ToString("N");
        public AutoRunPreviewRequest Request { get; } = request;
        public IReadOnlyList<AutoRunResolvedTarget> Targets { get; } = targets;
        public bool IsDirect { get; } = request.Targets.Count == 0;
        public int Maximum { get; } = maximum;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public ActivityContext TraceContext { get; } = Activity.Current?.Context ?? default;
        public AutoRunState State { get; set; } = AutoRunState.Preparing;
        public DateTimeOffset? ReadyAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? FinishedAt { get; set; }
        public CancellationTokenSource Cancellation { get; } = new();
        public List<Item> Items { get; set; } = [];
        public IReadOnlyList<JsonElement> Queries { get; set; } = [];
        public List<string> Warnings { get; } = [];
        public List<string> Errors { get; } = [];
        public AutoRunPublication Publication { get; set; } = request.Publication!;
        public AutoRunPublication ActivePublication { get; set; } = request.Publication!;
        public long RuleBytes { get; set; }
        public long PayloadBytes { get; set; }
        public int Scanned, Invalid, Duplicates, ObservedMatches, Confirmed, Failed, Uncertain, InFlight;
        public int[] SelectedByTarget { get; } = new int[targets.Count];
        public int[] ConfirmedByTarget { get; } = new int[targets.Count];
        public int[] FailedByTarget { get; } = new int[targets.Count];
        public int[] UncertainByTarget { get; } = new int[targets.Count];
        public bool SourceExhausted { get; set; }
        public string? StoppedReason { get; set; }
    }
}
