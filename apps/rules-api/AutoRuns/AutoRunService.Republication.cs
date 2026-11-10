using System.Text.Json;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed partial class AutoRunService
{
    public AutoRunSummary Republish(string id, CancellationToken cancellationToken)
    {
        if (!_options.Enabled) throw new AutoRunRequestException(503, "Historical auto-run is disabled.");
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            Sweep();
            var source = Find(id);
            EnsureNotDeleting(source);
            if (!CanRepublish(source))
                throw new AutoRunRequestException(409, "Only a completed run with retained selected overlays can prepare a publication rerun.");
            EnsureAccepting();
            if (_jobs.Count >= _options.MaxRetainedJobs)
                throw new AutoRunRequestException(409, "The retained-job limit is reached. Delete an unused run or wait for expiry.");
            if (source.PayloadBytes > _options.PayloadBudgetBytes - _retainedBytes)
                throw PayloadBudgetFull();
            // Include temporary serialization buffers and per-item metadata before copying payloads.
            EnsureCopyMemory(source.PayloadBytes + source.RuleBytes * 3 + source.Items.Count * 256L + 4096);

            long ruleBytes = 0;
            var request = Freeze(source.Request with { Publication = source.Publication with { } });
            var targets = source.Targets.Select(target => target with
            {
                Target = target.Target with { },
                Rule = Freeze(target.Rule)
            }).ToArray();
            var queries = source.Queries.Select(query => Freeze(query)).ToArray();
            if (ruleBytes + source.PayloadBytes > _options.PayloadBudgetBytes - _retainedBytes)
                throw PayloadBudgetFull();

            var items = new List<Item>(source.Items.Count);
            foreach (var item in source.Items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new(item.Id, item.SensorName, item.SensorType, item.PhotoTime,
                    item.Payload!.ToArray(), item.Assignments.ToArray()));
            }
            EnsureCopyMemory(0);
            cancellationToken.ThrowIfCancellationRequested();
            var now = time.GetUtcNow();
            var job = new Job(request, targets, now, source.Maximum)
            {
                State = AutoRunState.Ready,
                ReadyAt = now,
                WaitReason = null,
                RepublishedFromRunId = source.Id,
                Items = items,
                Queries = queries,
                RuleBytes = ruleBytes,
                PayloadBytes = source.PayloadBytes,
                Scanned = source.Scanned,
                Invalid = source.Invalid,
                Duplicates = source.Duplicates,
                ObservedMatches = source.ObservedMatches,
                SourceExhausted = source.SourceExhausted,
                StoppedReason = source.StoppedReason
            };
            Array.Copy(source.SelectedByTarget, job.SelectedByTarget, source.SelectedByTarget.Length);
            job.Warnings.AddRange(source.Warnings);
            AddWarning(job, "This preview reuses the original selected overlays without searching or reevaluating selection rules. Start resends every selected overlay; downstream deduplication may suppress repeat processing.");
            // Record admission only after the entire copy succeeds; cancellation/rejection leaves no job or byte charge.
            _jobs.Add(job.Id, job);
            _retainedBytes += job.RuleBytes + job.PayloadBytes;
            SelectedMetric.Add(job.Items.Count);
            logger.AutoRunPublicationCopied(job.Id, source.Id, job.Items.Count);
            return Summary(job);

            T Freeze<T>(T value)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = JsonSerializer.SerializeToUtf8Bytes(value, AutoRunJson.Options);
                ruleBytes += bytes.LongLength;
                return JsonSerializer.Deserialize<T>(bytes, AutoRunJson.Options)!;
            }
        }

        void EnsureCopyMemory(long additionalBytes)
        {
            if (memory?.CanAllocate(additionalBytes) == false)
                throw new AutoRunRequestException(409, "Memory is near the limit. Delete an unused run or wait before preparing a publication rerun.");
        }
    }

    private static bool CanRepublish(Job job) =>
        !job.DeletionRequested && job.State is AutoRunState.Completed or AutoRunState.CompletedWithErrors &&
        job.Items.Count > 0 && job.PayloadBytes > 0;

    private static AutoRunRequestException PayloadBudgetFull() => new(409,
        "The retained payload budget cannot fit another copy of this selection. Delete an unused run or wait for expiry.");
}
