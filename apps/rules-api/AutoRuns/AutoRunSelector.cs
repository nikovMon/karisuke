using ImagingPipeline.Rules.Api.AutoRuns.Provider;

namespace ImagingPipeline.Rules.Api.AutoRuns;

public sealed record AutoRunCandidate(ProviderOverlay Overlay, int[] Matches);
public sealed record AutoRunSelection(AutoRunCandidate Candidate, int[] Assignments);

public static class AutoRunSelector
{
    public static IReadOnlyList<AutoRunSelection> Select(
        IReadOnlyList<AutoRunCandidate> candidates, IReadOnlyList<AutoRunTarget> targets,
        int overallMaximum, CancellationToken cancellationToken = default)
    {
        var counts = new int[targets.Count];
        var selected = new List<AutoRunSelection>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var ordered = candidates.OrderByDescending(c => c.Overlay.Input.PhotoTime)
            .ThenBy(c => c.Overlay.Input.ImageId, StringComparer.Ordinal).ToArray();
        // Scores only decrease. Lazily refresh stale queue entries instead of rescanning
        // 200,000 candidates for every one of up to 100,000 selections.
        var queue = new PriorityQueue<int, (int Score, int Order)>();
        for (var i = 0; i < ordered.Length; i++)
        {
            var score = Score(ordered[i]);
            if (score > 0) queue.Enqueue(i, (-score, i));
        }
        while (selected.Count < overallMaximum && queue.TryDequeue(out var index, out var priority))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var candidate = ordered[index];
            var score = Score(candidate);
            if (score == 0) continue;
            if (-score != priority.Score)
            {
                queue.Enqueue(index, (-score, index));
                continue;
            }
            Assign(candidate);
        }
        foreach (var candidate in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (selected.Count >= overallMaximum) break;
            if (!used.Contains(candidate.Overlay.Input.ImageId)) Assign(candidate);
        }
        return selected;

        int Score(AutoRunCandidate candidate) =>
            candidate.Matches.Count(i => counts[i] < targets[i].MinOverlays && counts[i] < targets[i].MaxOverlays);

        void Assign(AutoRunCandidate candidate)
        {
            var assignments = candidate.Matches.Where(i => counts[i] < targets[i].MaxOverlays).ToArray();
            if (assignments.Length == 0) return;
            foreach (var i in assignments) counts[i]++;
            used.Add(candidate.Overlay.Input.ImageId);
            selected.Add(new(candidate, assignments));
        }
    }
}
