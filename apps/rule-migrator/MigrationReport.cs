namespace ImagingPipeline.RuleMigrator;

internal sealed record SkippedRule(string Id, string? RuleName, string Reason);

internal sealed record DroppedArea(string Id, string RuleName, string Area);

internal sealed class MigrationReport
{
    public int Read { get; set; }

    public List<string> Migrated { get; } = [];

    public List<SkippedRule> Skipped { get; } = [];

    public List<DroppedArea> DroppedAreas { get; } = [];

    public void Write(TextWriter writer, string source, string target, bool apply)
    {
        writer.WriteLine($"{source} -> {target}{(apply ? string.Empty : " (dry run, nothing written)")}");
        writer.WriteLine($"Read {Read}, {(apply ? "migrated" : "would migrate")} {Migrated.Count}, skipped {Skipped.Count}.");

        if (Skipped.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Skipped:");
            foreach (var rule in Skipped)
            {
                writer.WriteLine($"  {rule.Id} '{rule.RuleName}': {rule.Reason}");
            }
        }

        if (DroppedAreas.Count > 0)
        {
            writer.WriteLine();
            writer.WriteLine("Dropped area (no v2 field):");
            foreach (var area in DroppedAreas)
            {
                writer.WriteLine($"  {area.Id} '{area.RuleName}': {area.Area}");
            }
        }
    }
}
